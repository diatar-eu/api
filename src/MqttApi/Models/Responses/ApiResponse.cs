namespace MqttApi.Models.Responses;

public class ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }

    public static ApiResponse Ok(string message, object? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static ApiResponse Fail(string message) =>
        new() { Success = false, Message = message };
}
