using AgarIA.Web.Services.FlashMessage.Contracts;

namespace AgarIA.Web.Services.FlashMessage;

public static class FlashMessageExtensions {
    public static void AddFlashMessage(this IServiceCollection services) {
        services.AddTransient<IFlashMessage, FlashMessage>();
        services.AddTransient<IFlashMessageSerializer, JsonFlashMessageSerializer>();
    }
}
