using System.ComponentModel.DataAnnotations;

namespace AgarIA.Web.Data;

public class AdminSetting
{
    [Key]
    [MaxLength(64)]
    public string Key { get; set; }

    public string Value { get; set; }
}
