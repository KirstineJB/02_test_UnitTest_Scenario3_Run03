using ConsoleApp.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Models
{
    public class User
    {
        private readonly HashSet<Role> _roles = new();

        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public UserTier Tier { get; init; }
        public bool IsAdmin { get; init; }

        public IReadOnlyCollection<Role> Roles => _roles;

        public User(Guid id, Guid tenantId, UserTier tier, bool isAdmin, IEnumerable<Role>? roles = null)
        {
            Id = id;
            TenantId = tenantId;
            Tier = tier;
            IsAdmin = isAdmin;

            if (roles is not null)
            {
                foreach (var role in roles)
                {
                    _roles.Add(role);
                }
            }
        }

        public bool HasRole(Role role) => _roles.Contains(role);
    }
}
