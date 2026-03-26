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

        var user = await _unitOfWork.Users.GetByIdAsync(parsedId);
        if (user is null)
            throw GrpcErrors.NotFound("User", request.Id);

        return new GetUserResponse
        {
            Id = user.Id.ToString(),
            Email = user.Email,
            FullName = user.FullName
        };
    }
}
