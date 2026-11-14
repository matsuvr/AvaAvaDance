using System.Collections.Generic;
using System.Linq;

namespace MaterialLabels
{
    public static class Summary
    {
        public static string Format(IEnumerable<string> labels)
        {
            var entries=labels.ToList();
            return entries.Count+" 材質スロット　"+string.Join("、",entries);
        }
    }
}
