namespace Wawshni.Bot;

public sealed class BotContactOptions
{
    public const string SectionName = "BotContact";

    public string DeveloperEmail { get; set; } = "";

    public string RejectedAccessNotice
    {
        get
        {
            var email = DeveloperEmail?.Trim();
            var contact = string.IsNullOrWhiteSpace(email)
                ? "إذا عندك استفسار أو بتعتقد أنه صار خطأ، تواصل مع مسؤول وشوشني."
                : $"إذا عندك استفسار أو بتعتقد أنه صار خطأ، تواصل مع المهندس محمد: {email}";

            return $"هههههههههههههههههههههههههههههههه يا هلفوت فش استخدام للبوت بكفي , روح افتح المودل\nحسابك حالياً غير معتمد لاستخدام وشوشني ارسل /my_access لمعرفة الحالة، {contact}";
        }
    }
}
