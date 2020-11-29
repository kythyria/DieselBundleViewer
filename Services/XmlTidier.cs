using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DieselBundleViewer.Services
{
    static class XmlTidier
    {
        // Match comments how people generally think they work rather than how they actually work
        // they do work more like this in HTML5.
        private static readonly Regex LooseComment = new Regex("<!--(?:.|[\r\n])*?-->");

        /// <summary>
        /// Attempts to make well-formed XML out of things Diesel accepts.
        /// </summary>
        /// <remarks>
        /// This isn't a terribly clever function, it just applies string manipulation that makes the errors go away.
        /// </remarks>
        public static string TidyString(string input)
        {
            // Nothing before the first < or after the last > can be relevant.
            var firstLt = input.IndexOf('<');
            var lastGt = input.LastIndexOf('>');
            var trimmed = input.Substring(firstLt, lastGt - firstLt + 1);

            // remove all comments
            var commentless = LooseComment.Replace(trimmed, "");
            return commentless;
        }
    }
}
