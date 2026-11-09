namespace ARBM.CableJournalExporter.Services
{
    public class TemplateValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }

        public static TemplateValidationResult Ok()
        {
            return new TemplateValidationResult
            {
                IsValid = true,
                Message = "OK"
            };
        }

        public static TemplateValidationResult Fail(string message)
        {
            return new TemplateValidationResult
            {
                IsValid = false,
                Message = message
            };
        }
    }
}
