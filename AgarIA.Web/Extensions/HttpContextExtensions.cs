namespace AgarIA.Web.Extensions;

public static class HttpContextExtensions {
    public static string GetAction(this HttpContext httpContext) => httpContext.Request.RouteValues["Action"]?.ToString() ?? string.Empty;
    public static string GetController(this HttpContext httpContext) => httpContext.Request.RouteValues["Controller"]?.ToString() ?? string.Empty;
}
