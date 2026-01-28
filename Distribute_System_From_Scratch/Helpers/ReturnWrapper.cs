using System.Net;
using System.Text.Json;

namespace Distributed_System_From_Scratch.Helpers
{
    /// <summary>
    /// Helper class for wrapping return values with the appropriate
    /// container information.
    /// </summary>
    public static class ReturnWrapper
    {
        public static string WrapReturnMessage(this string message, char node, string path, int status)
        {
            var response = new Response()
            {
                Node = node,
                Path = path,
                StatusCode = status,
                Body = message
            };

            return JsonSerializer.Serialize(response);
        }

        private class Response
        {
            public char Node { get; set; }
            public int StatusCode { get; set; }
            public string? Path { get; set; }
            public string? Body { get; set; }
        }
    }
}
