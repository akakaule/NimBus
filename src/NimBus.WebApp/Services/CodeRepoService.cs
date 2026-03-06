using System;

namespace NimBus.WebApp.Services
{
    public interface ICodeRepoService
    {
        string GetSearchUrl(string className, string namespaceName);
        string CodeRepoUrl { get; }
    }

    internal class CodeRepoService : ICodeRepoService
    {
        public CodeRepoService(string codeRepoUrl)
        {
            CodeRepoUrl = codeRepoUrl;
        }

        public string CodeRepoUrl { get; }

        public string GetSearchUrl(string className, string namespaceName) =>
            $"{GetCodeRepoSearchBaseUrl()} class:{className} AND namespace:{namespaceName}";

        private string GetCodeRepoSearchBaseUrl() => $"{CodeRepoUrl.TrimEnd('/')}/_search?type=code&text=";
    }
}
