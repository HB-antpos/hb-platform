using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Mappings.Profiles
{
    /// <summary>
    /// 用户和角色映射配置
    /// 包含用户、角色、门店相关的实体与DTO的映射关系
    /// </summary>
    public class UserMappingProfile : BaseMappingProfile
    {
        public UserMappingProfile()
        {
            ConfigureUserMappings();
            ConfigureRoleMappings();
            ConfigureStoreMappings();
        }

        /// <summary>
        /// 配置用户相关映射
        /// </summary>
        private void ConfigureUserMappings()
        {
            // User -> UserDto 映射
            // 将User实体映射到UserDto，包括关联的角色和门店信息
            CreateMap<User, UserDto>()
                .ForMember(dest => dest.RoleNames, opt => opt.MapFrom(src => src.Roles != null ? src.Roles.Select(r => r.RoleName).ToList() : new List<string>()))
                .ForMember(dest => dest.StoreNames, opt => opt.MapFrom(src => src.Stores != null ? src.Stores.Select(s => s.StoreName).ToList() : new List<string>()))
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt ?? src.CreatedAt))
                .ForMember(dest => dest.Roles, opt => opt.MapFrom(src => src.Roles != null ? src.Roles.Select(r => new RoleDto
                {
                    RoleGUID = r.RoleGUID,
                    RoleName = r.RoleName,
                    Description = r.Description,
                    IsActive = r.IsActive,
                    Level = r.Level,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt ?? r.CreatedAt
                }).ToList() : new List<RoleDto>()))
                .ForMember(dest => dest.Stores, opt => opt.MapFrom(src => src.Stores != null ? src.Stores.Select(s => new UserStoreDto
                {
                    StoreGUID = s.StoreGUID,
                    StoreName = s.StoreName,
                    StoreCode = s.StoreCode,
                    IsPrimary = false,
                    AssignedAt = DateTime.UtcNow
                }).ToList() : new List<UserStoreDto>()));
        }

        /// <summary>
        /// 配置角色相关映射
        /// </summary>
        private void ConfigureRoleMappings()
        {
            // Role -> RoleDto 映射
            // 将Role实体映射到RoleDto
            CreateMap<Role, RoleDto>()
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt ?? src.CreatedAt))
                .ForMember(dest => dest.UserCount, opt => opt.MapFrom(src => src.Users != null ? src.Users.Count : 0));
        }

        /// <summary>
        /// 配置门店相关映射
        /// </summary>
        private void ConfigureStoreMappings()
        {
            // Store -> StoreDto 映射
            // 将Store实体映射到StoreDto
            CreateMap<Store, StoreDto>()
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt ?? src.CreatedAt))
                .ForMember(dest => dest.TotalUsers, opt => opt.MapFrom(src => src.Users != null ? src.Users.Count : 0))
                .ForMember(dest => dest.ActiveUsers, opt => opt.MapFrom(src => src.Users != null ? src.Users.Count(u => u.IsActive) : 0))
                .ForMember(dest => dest.ContactPhone, opt => opt.MapFrom(src => src.Phone))
                .ForMember(dest => dest.Description, opt => opt.Ignore()); // StoreDto有Description字段，但Store实体没有
        }
    }
}
