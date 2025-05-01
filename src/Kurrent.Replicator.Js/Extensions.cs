using System.Text;

namespace Kurrent.Replicator.Js; 

public static class Extensions {
    public static string AsUtf8String(this byte[] data) => Encoding.UTF8.GetString(data);
}