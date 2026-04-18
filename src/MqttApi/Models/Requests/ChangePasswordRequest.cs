using System.ComponentModel.DataAnnotations;

namespace MqttApi.Models.Requests;

public class ChangePasswordRequest
{
    [Required, MinLength(1), MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;
}
