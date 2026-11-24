using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CSharpMrp
{
    // UC 7.0.1.73's verified browsing request and uncompressed media:html page format.
    internal static class UcWebPage
    {
        internal static bool TryRequest(byte[] request, out string address)
        {
            address = "";
            if (request.Length > 32768)
            {
                return false;
            }
            string headers = Encoding.ASCII.GetString(request);
            int end = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }
            string[] lines = headers.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || first[0] != "POST" || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return false;
            }
            string host = "";
            int length = -1;
            foreach (string line in lines)
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    host = line.Substring(5).Trim();
                }
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    !int.TryParse(line.Substring(15).Trim(), out length))
                {
                    return false;
                }
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            if (!Uri.TryCreate(first[1].StartsWith("/", StringComparison.Ordinal) ?
                    "http://" + host + first[1] : first[1], UriKind.Absolute, out Uri? server) ||
                server.Scheme != "http" || server.UserInfo.Length != 0 ||
                !server.Host.EndsWith(".u2.ucweb.com", StringComparison.OrdinalIgnoreCase) ||
                length < 1 || end + 4 + length != request.Length)
            {
                return false;
            }
            int cursor = end + 4;
            bool version = false;
            bool browser = false;
            try
            {
                string ReadString()
                {
                    if (cursor + 2 > request.Length)
                    {
                        throw new FormatException();
                    }
                    int count = request[cursor] << 8 | request[cursor + 1];
                    cursor += 2;
                    if (cursor + count > request.Length)
                    {
                        throw new FormatException();
                    }
                    string value = new UTF8Encoding(false, true).GetString(request, cursor, count);
                    cursor += count;
                    return value;
                }
                while (cursor < request.Length)
                {
                    int tag = request[cursor++];
                    switch (tag)
                    {
                        case 0x0f:
                            if (cursor == request.Length || request[cursor++] != 1)
                            {
                                return false;
                            }
                            browser = true;
                            break;
                        case 0x11:
                            version = ReadString() == "7.0.1.73";
                            break;
                        case 0x13:
                        case 0x24:
                        case 0x4b:
                        case 0x4c:
                            ReadString();
                            break;
                        case 0x41:
                            if (address.Length != 0)
                            {
                                return false;
                            }
                            address = ReadString();
                            break;
                        case 0x49:
                            if (ReadString().Length != 0)
                            {
                                return false;
                            }
                            break;
                        case 0x53:
                            ReadString();
                            ReadString();
                            break;
                        case 0x12:
                        case 0x23:
                        case 0x25:
                        case 0x31:
                        case 0x32:
                        case 0x33:
                            cursor++;
                            break;
                        case 0x22:
                        case 0x34:
                            cursor += 2;
                            break;
                        case 0x71:
                            cursor += 4;
                            break;
                        case 0x40:
                            break;
                        default:
                            return false;
                    }
                    if (cursor > request.Length)
                    {
                        return false;
                    }
                }
                if (!browser || !version || address.Length == 0)
                {
                    return false;
                }
                return SimplifiedWebPage.Target(address).Host != "command";
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException)
            {
                address = "";
                return false;
            }
        }

        internal static byte[] Encode(string wml, string address)
        {
            using var reader = XmlReader.Create(new StringReader(wml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 100000
            });
            XElement card = XDocument.Load(reader).Root!.Element("card")!;
            using var page = new MemoryStream();
            void Bytes(params byte[] bytes)
            {
                page.Write(bytes, 0, bytes.Length);
            }
            void Word(int value, int bytes)
            {
                for (int shift = (bytes - 1) * 8; shift >= 0; shift -= 8)
                {
                    page.WriteByte((byte)(value >> shift));
                }
            }
            void String(string value, int lengthBytes)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                Word(bytes.Length, lengthBytes);
                page.Write(bytes, 0, bytes.Length);
            }
            void Text(string value, bool link = false)
            {
                if (value == "仅文字、链接和 GET 搜索；脚本与登录不可用。")
                {
                    value = "仅静态文字和链接；脚本、登录及表单不可用。";
                }
                if (value.Length == 0)
                {
                    return;
                }
                Bytes(0x62, 0x3c, 0);
                Bytes(link ? new byte[] { 9, 119, 231, 255 } : new byte[] { 67, 80, 90, 255 });
                String(value.Replace("$$", "$"), 3);
                Bytes(0xda);
            }
            void Nodes(XContainer container)
            {
                foreach (XNode node in container.Nodes())
                {
                    if (node is XText text)
                    {
                        Text(text.Value);
                    }
                    else if (node is XElement element)
                    {
                        switch (element.Name.LocalName)
                        {
                            case "br":
                                Bytes(1);
                                break;
                            case "a":
                                Bytes(0x40, 0x50);
                                String(element.Attribute("href")!.Value, 2);
                                Bytes(255);
                                Text(element.Value, true);
                                Bytes(0xfc);
                                break;
                            case "input":
                                Text("[UC 简化页暂不支持表单输入]");
                                break;
                            case "anchor":
                                Text("[UC 简化页暂不支持表单提交]");
                                break;
                            default:
                                Nodes(element);
                                break;
                        }
                    }
                }
            }
            Bytes(0x1a, 2);
            String(card.Attribute("title")?.Value ?? "简化浏览", 3);
            Bytes(0xfe, 0x60, 0x3c, 0, 255, 255, 255, 255);
            Nodes(card);
            Bytes(0xdc, 0xe6, 255, 255, 255, 255);
            byte[] native = page.ToArray();
            page.SetLength(0);
            page.Position = 0;
            Word(0, 4); // No cloud metadata.
            Bytes(0);
            String(address, 2);
            Bytes(255);
            Word(1, 4); // One resource.
            Word(native.Length + 8, 4);
            String("media:html", 2);
            Word(0, 4); // Uncompressed resource.
            Word(native.Length, 4);
            page.Write(native, 0, native.Length);
            return page.ToArray();
        }
    }
}
