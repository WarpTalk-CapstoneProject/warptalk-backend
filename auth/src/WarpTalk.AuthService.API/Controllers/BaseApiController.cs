using System;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
public abstract class BaseApiController : ControllerBase
{
    protected Guid CurrentUserId => User.GetUserId() ?? throw new UnauthorizedAccessException("User is not authenticated or user context is missing.");
}
