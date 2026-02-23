using System.ComponentModel.DataAnnotations;

namespace AgarIA.Web.Dtos;

public class ChangePasswordDto {
    [Required]
    [DataType(DataType.Password)]
    [MinLength(4)]
    [Display(Name = "New Password")]
    public string NewPassword { get; set; }

    [Required]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword))]
    [Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; }
}
