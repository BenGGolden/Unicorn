﻿using System;
using Unicorn.Roles.Model;
using System.Collections.Generic;
using System.Linq;
using Sitecore.Diagnostics;
using Sitecore.Security.Accounts;
using Unicorn.Configuration;
using Unicorn.Logging;
using Unicorn.Roles.Data;
using Unicorn.Roles.RolePredicates;

namespace Unicorn.Roles.Loader
{
	public class RoleLoader : IRoleLoader
	{
		private readonly IRolePredicate _rolePredicate;
		private readonly IRoleDataStore _roleDataStore;
		private readonly IRoleLoaderLogger _loaderLogger;
		private readonly IRoleSyncConfiguration _syncConfiguration;

		public RoleLoader(IRolePredicate rolePredicate, IRoleDataStore roleDataStore, IRoleLoaderLogger loaderLogger, IRoleSyncConfiguration syncConfiguration)
		{
			Assert.ArgumentNotNull(rolePredicate, nameof(rolePredicate));
			Assert.ArgumentNotNull(roleDataStore, nameof(roleDataStore));
			Assert.ArgumentNotNull(loaderLogger, nameof(loaderLogger));

			_rolePredicate = rolePredicate;
			_roleDataStore = roleDataStore;
			_loaderLogger = loaderLogger;
			_syncConfiguration = syncConfiguration;
		}

		public virtual void Load(IConfiguration configuration)
		{
			using (new UnicornOperationContext())
			{
				var roles = _roleDataStore
					.GetAll()
					.Where(role => _rolePredicate.Includes(role).IsIncluded)
					.ToArray();

				foreach (var role in roles)
				{
					DeserializeRole(role);
				}

				if (_syncConfiguration.RemoveOrphans)
				{
					EvaluateOrphans(roles);
				}
			}
		}

		protected virtual void DeserializeRole(IRoleData role)
		{
			bool addedRole = false;

			// Add role if needed
			var name = role.RoleName;
			if (!System.Web.Security.Roles.RoleExists(name))
			{
				_loaderLogger.AddedNewRole(role);
				addedRole = true;
				System.Web.Security.Roles.CreateRole(name);
			}

			Role targetRole = Role.FromName(name);
			var currentSourceParents = new SitecoreRoleData(targetRole).MemberOfRoles;
			var currentTargetParents = role.MemberOfRoles;

			var addedRoleMembership = new List<string>();
			var removedRoleMembership = new List<string>();
			var deferredUpdateLog = new DeferredLogWriter<IRoleLoaderLogger>();

			// Loop over the serialized parent roles and set db roles if needed
			foreach (var serializedMemberRoleName in currentTargetParents)
			{
				var memberRole = Role.FromName(serializedMemberRoleName);

				// add nonexistant parent role if needed. NOTE: parent role need not be one we have serialized or included.
				if (!Role.Exists(serializedMemberRoleName))
				{
					deferredUpdateLog.AddEntry(log => log.AddedNewRoleMembership(new SitecoreRoleData(memberRole)));
					System.Web.Security.Roles.CreateRole(serializedMemberRoleName);
				}

				// Add membership if not already in the parent role
				if (!RolesInRolesManager.IsRoleInRole(targetRole, memberRole, false))
				{
					addedRoleMembership.Add(memberRole.Name);
					RolesInRolesManager.AddRoleToRole(targetRole, memberRole);
				}
			}

			// Loop over parent roles that exist in the database but not in serialized and remove them
			var membershipToRemove = currentSourceParents.Where(parent => !currentTargetParents.Contains(parent, StringComparer.OrdinalIgnoreCase));
			foreach (var roleToRemove in membershipToRemove)
			{
				removedRoleMembership.Add(roleToRemove);
				RolesInRolesManager.RemoveRoleFromRole(targetRole, Role.FromName(roleToRemove));
			}

			if (!addedRole && (addedRoleMembership.Count > 0 || removedRoleMembership.Count > 0))
			{
				_loaderLogger.RoleMembershipChanged(role, addedRoleMembership.ToArray(), removedRoleMembership.ToArray());
			}

			deferredUpdateLog.ExecuteDeferredActions(_loaderLogger);
		}

		protected virtual void EvaluateOrphans(IRoleData[] roles)
		{
			var knownRoles = roles.ToLookup(key => key.RoleName);

			var allOrphanRoles = RolesInRolesManager.GetAllRoles(false)
				.Select(role => new SitecoreRoleData(role))
				.Where(role => _rolePredicate.Includes(role).IsIncluded)
				.Where(role => !knownRoles.Contains(role.RoleName))
				.ToArray();

			foreach (var orphan in allOrphanRoles)
			{
				_loaderLogger.RemovedOrphanRole(orphan);
				System.Web.Security.Roles.DeleteRole(orphan.RoleName);
			}
		}
	}
}

