using AgarIA.Web.Services.FlashMessage.Contracts;

namespace AgarIA.Web.Services.FlashMessage;

public class FlashMessageModel : IFlashMessageModel {
    public bool IsHtml { get; set; }
    public string Title { get; set; }
    public string Message { get; set; }
    public FlashMessageType Type { get; set; } = FlashMessageType.Success;
}
