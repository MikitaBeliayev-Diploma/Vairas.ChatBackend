using System.Security.Claims;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend;

public static class ActorClaims
{
    public static ChatActor Actor(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedException("AUTHENTICATION_REQUIRED");
        return new(Guid.Parse(http.User.FindFirstValue("sub")!), Guid.Parse(http.User.FindFirstValue("sid")!), Guid.Empty, header[7..], DateTimeOffset.MinValue);
    }
}
