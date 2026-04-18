using System.ComponentModel.DataAnnotations;

namespace MqttApi.Models.Requests;

public class ResendVerificationRequest
{
    [Required, MinLength(1), MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;
}
