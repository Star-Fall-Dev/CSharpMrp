using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CSharpMrp
{
    // Produces static WML accepted by the original browser, with no script or active HTML attributes.
    internal static class SimplifiedWebPage
    {
        private const string Gateway = "http://mrp-web.invalid/?u=";
        private const int MaximumCharacters = 12000;
        private static readonly Regex Attributes = new Regex(
            @"([a-zA-Z_][\w:.-]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        private sealed class Form
        {
            internal string Action = "";
            internal bool Blocked;
            internal string Submit = "提交";
            internal readonly List<Field> Fields = new List<Field>();
        }

        private sealed class Field
        {
            internal string Name = "";
            internal string Value = "";
            internal string Variable = "";
            internal string Label = "输入内容";
            internal bool Hidden;
        }

        internal static Uri Validate(Uri target)
        {
            if (!target.IsAbsoluteUri || (target.Scheme != "http" && target.Scheme != "https") ||
                target.Host.Length == 0 || target.UserInfo.Length != 0 || target.AbsoluteUri.Length > 4096)
            {
                throw new ArgumentException("简化浏览只支持 HTTP/HTTPS 网页地址。");
            }
            return target;
        }

        internal static Uri Target(string address)
        {
            if (address.StartsWith("http://https://", StringComparison.OrdinalIgnoreCase))
            {
                address = address.Substring(7);
            }
            Uri target = Validate(new Uri(address, UriKind.Absolute));
            if (target.Host != "mrp-web.invalid")
            {
                return target;
            }
            string? original = null;
            var fields = new List<string>();
            foreach (string pair in target.Query.TrimStart('?').Split('&'))
            {
                int equal = pair.IndexOf('=');
                string name = Uri.UnescapeDataString((equal < 0 ? pair : pair.Substring(0, equal)).Replace("+", " "));
                string value = equal < 0 ? "" : Uri.UnescapeDataString(pair.Substring(equal + 1).Replace("+", " "));
                if (name == "u" && original == null)
                {
                    original = value;
                }
                else if (name.Length != 0)
                {
                    fields.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
                }
            }
            if (original == null)
            {
                throw new ArgumentException("简化页面链接缺少目标地址。");
            }
            Uri decoded = Validate(new Uri(original, UriKind.Absolute));
            if (decoded.Host == "mrp-web.invalid")
            {
                throw new ArgumentException("简化页面链接不能递归引用网关。");
            }
            var builder = new UriBuilder(decoded) { Fragment = "" };
            if (fields.Count != 0)
            {
                string query = builder.Query.TrimStart('?');
                builder.Query = (query.Length == 0 ? "" : query + "&") + string.Join("&", fields);
            }
            return Validate(builder.Uri);
        }

        private static string? Link(string address, Uri source)
        {
            try
            {
                Uri target = Validate(new Uri(source, address));
                // Fragments cannot refer to the HTML DOM after static conversion.
                var builder = new UriBuilder(target) { Fragment = "" };
                string link = Gateway + Uri.EscapeDataString(builder.Uri.AbsoluteUri);
                return link.Length <= 1000 ? link : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        private static Dictionary<string, string> ParseAttributes(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Attributes.Matches(text))
            {
                string name = match.Groups[1].Value;
                string value = match.Groups[2].Success ? match.Groups[2].Value :
                    match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                if (!values.ContainsKey(name))
                {
                    values.Add(name, WebUtility.HtmlDecode(value));
                }
            }
            return values;
        }

        private static string Attribute(Dictionary<string, string> values, string name)
        {
            return values.TryGetValue(name, out string? value) ? value : "";
        }

        private static string Clean(string value, int maximum = MaximumCharacters, bool decode = true)
        {
            if (decode)
            {
                value = WebUtility.HtmlDecode(value);
            }
            var text = new StringBuilder(Math.Min(value.Length, maximum));
            bool space = false;
            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    space = text.Length != 0;
                }
                else if (character >= 32 && !char.IsSurrogate(character) && character != '\ufffe' && character != '\uffff')
                {
                    if (space)
                    {
                        text.Append(' ');
                        space = false;
                    }
                    text.Append(character);
                }
                if (text.Length >= maximum)
                {
                    break;
                }
            }
            return text.ToString();
        }

        private static string Escape(string value, bool literal = true)
        {
            string escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
            return literal ? escaped.Replace("$", "$$") : escaped;
        }

        private static string Wrap(string title, string content)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><wml><card title=\"" +
                Escape(Clean(title, 32, false)) + "\">" + content + "</card></wml>";
        }

        internal static string Error(string reason)
        {
            return Wrap("简化浏览", "网页获取失败：<br/>" + Escape(Clean(reason, 300, false)) + "<br/>可后退或重新输入网址。");
        }

        private static string RenderForm(Form form, Uri source)
        {
            string? link = Link(form.Action, source);
            if (form.Blocked || link == null)
            {
                return "[此表单不支持简化浏览，仅支持 GET 搜索。]<br/>";
            }
            var text = new StringBuilder();
            foreach (Field field in form.Fields)
            {
                if (!field.Hidden)
                {
                    text.Append(Escape(Clean(field.Label, 48, false))).Append("：<input name=\"")
                        .Append(field.Variable).Append("\" title=\"").Append(Escape(Clean(field.Label, 48, false)))
                        .Append("\" value=\"").Append(Escape(Clean(field.Value, 255, false)))
                        .Append("\" maxlength=\"255\"/><br/>");
                }
            }
            text.Append("<anchor>").Append(Escape(Clean(form.Submit, 32, false))).Append("<go href=\"")
                .Append(Escape(link)).Append("\" method=\"get\">");
            foreach (Field field in form.Fields)
            {
                string value = field.Hidden ? Escape(Clean(field.Value, 1024, false)) : "$(" + field.Variable + ")";
                text.Append("<postfield name=\"").Append(Escape(field.Name)).Append("\" value=\"")
                    .Append(value).Append("\"/>");
            }
            return text.Append("</go></anchor><br/>").ToString();
        }

        internal static string Convert(string html, Uri source, int status, bool plain)
        {
            string title = source.Host;
            var body = new StringBuilder();
            int visible = 0;
            int links = 0;
            int variable = 0;
            bool truncated = false;
            bool anchor = false;
            string description = "";
            string? skipped = null;
            int skipDepth = 0;
            Form? form = null;
            void Markup(string value)
            {
                if (value == "<br/>" && (body.Length == 0 ||
                    body.Length >= 5 && body.ToString(body.Length - 5, 5) == "<br/>"))
                {
                    return;
                }
                if (body.Length + value.Length > MaximumCharacters)
                {
                    truncated = true;
                }
                else
                {
                    body.Append(value);
                }
            }
            void Text(string value, bool decode = true)
            {
                value = Clean(value, MaximumCharacters, decode);
                if (value.Length == 0)
                {
                    return;
                }
                int remaining = MaximumCharacters - body.Length - 32;
                string escaped = Escape(value);
                while (escaped.Length > remaining && value.Length != 0)
                {
                    truncated = true;
                    value = value.Substring(0, Math.Max(0, value.Length * Math.Max(0, remaining) / escaped.Length - 1));
                    escaped = Escape(value);
                }
                visible += value.Length;
                body.Append(escaped).Append(' ');
            }
            void CloseAnchor()
            {
                if (anchor)
                {
                    body.Append("</a>");
                    anchor = false;
                }
            }
            if (plain)
            {
                Text(html, false);
            }
            else
            {
                int cursor = 0;
                while (cursor < html.Length && !truncated)
                {
                    int start = html.IndexOf('<', cursor);
                    if (start < 0)
                    {
                        if (skipped == null)
                        {
                            Text(html.Substring(cursor));
                        }
                        break;
                    }
                    bool separatedLinks = false;
                    if (skipped == null)
                    {
                        string between = html.Substring(cursor, start - cursor);
                        separatedLinks = !anchor && body.Length >= 4 &&
                            body.ToString(body.Length - 4, 4) == "</a>" && between.Length != 0 &&
                            string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(between));
                        Text(between);
                    }
                    if (html.IndexOf("<!--", start, StringComparison.Ordinal) == start)
                    {
                        int commentEnd = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                        cursor = commentEnd < 0 ? html.Length : commentEnd + 3;
                        continue;
                    }
                    int end = start + 1;
                    char quote = '\0';
                    for (; end < html.Length; end++)
                    {
                        char character = html[end];
                        if (quote != '\0')
                        {
                            if (character == quote)
                            {
                                quote = '\0';
                            }
                        }
                        else if (character == '\'' || character == '"')
                        {
                            quote = character;
                        }
                        else if (character == '>')
                        {
                            break;
                        }
                    }
                    if (end == html.Length)
                    {
                        break;
                    }
                    cursor = end + 1;
                    string tag = html.Substring(start + 1, end - start - 1).Trim();
                    bool closing = tag.StartsWith("/", StringComparison.Ordinal);
                    if (closing)
                    {
                        tag = tag.Substring(1).TrimStart();
                    }
                    int nameEnd = 0;
                    while (nameEnd < tag.Length && char.IsLetterOrDigit(tag[nameEnd]))
                    {
                        nameEnd++;
                    }
                    string name = tag.Substring(0, nameEnd).ToLowerInvariant();
                    if (!closing && (name == "script" || name == "style" || name == "title"))
                    {
                        int closingTag = html.IndexOf("</" + name, cursor, StringComparison.OrdinalIgnoreCase);
                        if (name == "title" && closingTag >= 0)
                        {
                            title = Clean(html.Substring(cursor, closingTag - cursor), 64);
                        }
                        cursor = closingTag < 0 ? html.Length : closingTag;
                        continue;
                    }
                    var attributes = ParseAttributes(tag.Substring(nameEnd));
                    if (name == "meta" && Attribute(attributes, "name").Equals("description", StringComparison.OrdinalIgnoreCase))
                    {
                        description = Attribute(attributes, "content");
                    }
                    if (skipped != null)
                    {
                        if (name == skipped)
                        {
                            skipDepth += closing ? -1 : 1;
                            if (skipDepth == 0)
                            {
                                skipped = null;
                            }
                        }
                        continue;
                    }
                    if (!closing && (name == "head" || name == "script" || name == "style" ||
                        name == "template" || name == "svg" || name == "iframe" ||
                        attributes.ContainsKey("hidden") || Attribute(attributes, "style").Replace(" ", "").Contains("display:none")))
                    {
                        skipped = name;
                        skipDepth = 1;
                        continue;
                    }
                    if (name == "a")
                    {
                        CloseAnchor();
                        if (!closing && links < 80)
                        {
                            string? link = Link(Attribute(attributes, "href"), source);
                            if (link != null && body.Length + link.Length + 64 < MaximumCharacters)
                            {
                                if (separatedLinks)
                                {
                                    // WML drops ordinary whitespace at element boundaries.
                                    Markup("&#160;");
                                }
                                Markup("<a href=\"" + Escape(link) + "\">");
                                anchor = true;
                                links++;
                            }
                        }
                    }
                    else if (name == "form")
                    {
                        CloseAnchor();
                        if (closing && form != null)
                        {
                            Markup(RenderForm(form, source));
                            form = null;
                        }
                        else if (!closing)
                        {
                            string method = Attribute(attributes, "method");
                            form = new Form { Action = Attribute(attributes, "action"),
                                Blocked = method.Length != 0 && !method.Equals("get", StringComparison.OrdinalIgnoreCase) };
                        }
                    }
                    else if (name == "input" && !closing && form != null && form.Fields.Count < 16)
                    {
                        string type = Attribute(attributes, "type").ToLowerInvariant();
                        if (type == "password" || type == "file")
                        {
                            form.Blocked = true;
                        }
                        else if (type == "submit")
                        {
                            form.Submit = Attribute(attributes, "value");
                        }
                        else if (type == "" || type == "text" || type == "search" || type == "hidden")
                        {
                            string fieldName = Attribute(attributes, "name");
                            if (fieldName.Length != 0 && fieldName.Length <= 64 &&
                                Clean(fieldName, 64, false) == fieldName)
                            {
                                string label = Attribute(attributes, "placeholder");
                                form.Fields.Add(new Field { Name = fieldName, Value = Attribute(attributes, "value"),
                                    Variable = "v" + variable++, Hidden = type == "hidden",
                                    Label = label.Length == 0 ? "输入内容" : label });
                            }
                        }
                    }
                    else if (name == "img" && !closing)
                    {
                        Text(Attribute(attributes, "alt"), false);
                    }
                    else if (name == "br" || name == "p" || name == "div" || name == "li" ||
                        name == "tr" || name == "hr" || name == "h1" || name == "h2" || name == "h3")
                    {
                        Markup("<br/>");
                    }
                }
                CloseAnchor();
                if (form != null)
                {
                    Markup(RenderForm(form, source));
                }
            }
            if (visible == 0)
            {
                Text(description, false);
                Markup("<br/>该页依赖动态脚本或没有可提取的正文。<br/>");
            }
            if (truncated)
            {
                body.Append("<br/>[页面较长，后续内容已省略。]");
            }
            string notice = "仅文字、链接和 GET 搜索；脚本与登录不可用。<br/>" + Escape(source.Host) + "<br/>";
            if (status < 200 || status >= 300)
            {
                notice += "原站点返回 HTTP " + status + "。<br/>";
            }
            return Wrap("简化页：" + title, notice + body);
        }
    }
}
