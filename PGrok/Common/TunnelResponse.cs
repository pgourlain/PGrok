using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace PGrok.Common.Models;

public class TunnelResponse
{
    public int StatusCode { get; set; }
    public Dictionary<string, string> Headers { get; set; }
    public string? Body { get; set; }

    public static TunnelResponse FromException(Exception ex)
    {
        return new TunnelResponse {
            StatusCode = 500,
            Headers = new Dictionary<string, string>(),
            Body = $"Error: {ex.Message}"
        };
    }
}
