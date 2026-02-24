namespace AgarIA.Web.Services.FlashMessage.Contracts;

public interface IFlashMessageSerializer {
    List<IFlashMessageModel> Deserialize(string data);
    string Serialize(IList<IFlashMessageModel> messages);
}
