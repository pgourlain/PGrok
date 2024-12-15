using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Common.Models;

public class TunnelTcpMessage
{
    public string Type { get; set; } = string.Empty;        // "data" ou "control"
    public string ConnectionId { get; set; } = string.Empty; // Identifiant unique de la connexion TCP
    public string? Data { get; set; }                       // Données encodées en base64
}

