using System.ComponentModel.DataAnnotations;

namespace MqttApi.Models.Requests;

public class ResetPasswordRequest
{
    [Required, MinLength(1), MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(512)]
    public string Token { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;
}
