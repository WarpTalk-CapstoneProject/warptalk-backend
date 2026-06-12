using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.API.GrpcServices;

public class UserServiceGrpc : UserService.UserServiceBase
{
    private readonly IUnitOfWork _unitOfWork;

    public UserServiceGrpc(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task<GetUserResponse> GetUserById(GetUserRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("User");

        var user = await _unitOfWork.UserRepository.GetByIdAsync(parsedId);
        if (user is null)
            throw GrpcErrors.NotFound("User", request.Id);

        return new GetUserResponse
        {
            Id = user.Id.ToString(),
            Email = user.Email,
            FullName = user.FullName,
            AvatarUrl = user.AvatarUrl ?? "",
            PreferredLanguage = user.PreferredLanguage ?? "en"
        };
    }

    public override async Task<GetUserResponse> GetUserByEmail(GetUserByEmailRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Email is required."));

        var user = await _unitOfWork.UserRepository.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (user is null)
            throw GrpcErrors.NotFound("User", request.Email);

        return new GetUserResponse
        {
            Id = user.Id.ToString(),
            Email = user.Email,
            FullName = user.FullName,
            AvatarUrl = user.AvatarUrl ?? "",
            PreferredLanguage = user.PreferredLanguage ?? "en"
        };
    }

    public override async Task<GetRoleResponse> GetRoleByName(GetRoleByNameRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Role name is required."));

        var role = await _unitOfWork.RoleRepository.FirstOrDefaultAsync(r => r.Name == request.Name);
        if (role is null)
            throw GrpcErrors.NotFound("Role", request.Name);

        return new GetRoleResponse
        {
            Id = role.Id.ToString(),
            Name = role.Name,
            Description = role.Description ?? ""
        };
    }

    public override async Task<GetRoleResponse> GetRoleById(GetRoleByIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("Role");

        var role = await _unitOfWork.RoleRepository.GetByIdAsync(parsedId);
        if (role is null)
            throw GrpcErrors.NotFound("Role", request.Id);

        return new GetRoleResponse
        {
            Id = role.Id.ToString(),
            Name = role.Name,
            Description = role.Description ?? ""
        };
    }
}
