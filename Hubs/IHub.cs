public interface IHub{
    Task ReceiveMessage(object data);
}