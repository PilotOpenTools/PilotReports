using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Reports.Tests
{
    /// <summary>Тест, которому нужен внешний сервис: без переменной окружения он пропускается (Skipped), а не проходит вхолостую.</summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public IntegrationFactAttribute(string environmentVariable, [CallerFilePath] string sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(environmentVariable)))
                Skip = $"Задайте {environmentVariable}, чтобы запустить интеграционный тест";
        }
    }
}
