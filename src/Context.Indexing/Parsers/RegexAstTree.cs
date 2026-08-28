using System.Text;
using System.Text.RegularExpressions;
using Context.Domain.Enums;
using Context.Indexing.Abstractions;

namespace Context.Indexing.Parsers;

/// <summary>
/// Internal parse tree produced by <see cref="RegexHeuristicParser"/>. This is the "Tree" object
/// boxed inside <see cref="ParsedAst"/> — opaque to everyone except that same parser. See the
/// header comment on <see cref="RegexHeuristicParser"/> for the scope and known limitations of
/// this MVP substitute for a real tree-sitter grammar.
/// </summary>
internal sealed class RegexAstTree
{
    public IReadOnlyList<SymbolDraft> Symbols { get; }
    public IReadOnlyList<RelationDraft> Relations { get; }

    private RegexAstTree(IReadOnlyList<SymbolDraft> symbols, IReadOnlyList<RelationDraft> relations)
    {
        Symbols = symbols;
        Relations = relations;
    }

    public static RegexAstTree Build(CodeDialect dialect, string sourceText)
    {
        var lines = NormalizeLines(sourceText);
        var cleanLines = StripCommentsAndStrings(lines, dialect);

        var (symbols, relations) = dialect == CodeDialect.Python
            ? ExtractPython(lines, cleanLines)
            : ExtractCurly(lines, cleanLines, dialect);

        return new RegexAstTree(symbols, relations);
    }

    private static string[] NormalizeLines(string sourceText) =>
        sourceText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ------------------------------------------------------------------
    // Comment / string stripping — blanks out comment and string-literal
    // content (keeping line count identical) so brace-counting and header
    // regexes never trip over "{" or keyword-looking text inside a string
    // or comment. Deliberately simplified: multi-line verbatim C# strings
    // and "${...}" interpolation inside template literals are treated as
    // opaque rather than fully re-entering code state — a documented MVP gap.
    // ------------------------------------------------------------------

    private enum ScanState { Code, BlockComment, TemplateLiteral, TripleQuote }

    private static string[] StripCommentsAndStrings(string[] lines, CodeDialect dialect)
    {
        var result = new string[lines.Length];
        var state = ScanState.Code;
        var tripleQuoteChar = '"';

        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var sb = new StringBuilder(line.Length);
            var i = 0;

            while (i < line.Length)
            {
                switch (state)
                {
                    case ScanState.BlockComment:
                    {
                        var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                        if (end < 0) { sb.Append(' ', line.Length - i); i = line.Length; }
                        else { sb.Append(' ', end + 2 - i); i = end + 2; state = ScanState.Code; }
                        break;
                    }
                    case ScanState.TemplateLiteral:
                    {
                        var end = FindUnescapedChar(line, i, '`');
                        if (end < 0) { sb.Append(' ', line.Length - i); i = line.Length; }
                        else { sb.Append(' ', end + 1 - i); i = end + 1; state = ScanState.Code; }
                        break;
                    }
                    case ScanState.TripleQuote:
                    {
                        var marker = new string(tripleQuoteChar, 3);
                        var end = line.IndexOf(marker, i, StringComparison.Ordinal);
                        if (end < 0) { sb.Append(' ', line.Length - i); i = line.Length; }
                        else { sb.Append(' ', end + 3 - i); i = end + 3; state = ScanState.Code; }
                        break;
                    }
                    default:
                    {
                        var c = line[i];
                        if (dialect == CodeDialect.Python && c == '#')
                        {
                            sb.Append(' ', line.Length - i);
                            i = line.Length;
                        }
                        else if (dialect != CodeDialect.Python && c == '/' && Peek(line, i + 1) == '/')
                        {
                            sb.Append(' ', line.Length - i);
                            i = line.Length;
                        }
                        else if (dialect != CodeDialect.Python && c == '/' && Peek(line, i + 1) == '*')
                        {
                            var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                            if (end < 0) { sb.Append(' ', line.Length - i); i = line.Length; state = ScanState.BlockComment; }
                            else { sb.Append(' ', end + 2 - i); i = end + 2; }
                        }
                        else if (dialect == CodeDialect.Python && (Match3(line, i, '"') || Match3(line, i, '\'')))
                        {
                            tripleQuoteChar = line[i];
                            sb.Append(' ', 3);
                            i += 3;
                            state = ScanState.TripleQuote;
                        }
                        else if (dialect != CodeDialect.Python && c == '`')
                        {
                            sb.Append(' ');
                            i++;
                            state = ScanState.TemplateLiteral;
                        }
                        else if (c == '"' || c == '\'')
                        {
                            var quote = c;
                            var verbatim = dialect == CodeDialect.CSharp && quote == '"' && i > 0 && line[i - 1] == '@';
                            sb.Append(' ');
                            i++;
                            while (i < line.Length)
                            {
                                if (!verbatim && line[i] == '\\' && i + 1 < line.Length)
                                {
                                    sb.Append(' ', 2);
                                    i += 2;
                                    continue;
                                }
                                if (line[i] == quote)
                                {
                                    if (verbatim && i + 1 < line.Length && line[i + 1] == quote)
                                    {
                                        sb.Append(' ', 2);
                                        i += 2;
                                        continue;
                                    }
                                    sb.Append(' ');
                                    i++;
                                    break;
                                }
                                sb.Append(' ');
                                i++;
                            }
                        }
                        else
                        {
                            sb.Append(c);
                            i++;
                        }
                        break;
                    }
                }
            }

            result[li] = sb.ToString();
        }

        return result;
    }

    private static char Peek(string line, int i) => i < line.Length ? line[i] : '\0';

    private static bool Match3(string line, int i, char q) =>
        i + 3 <= line.Length && line[i] == q && line[i + 1] == q && line[i + 2] == q;

    private static int FindUnescapedChar(string line, int start, char target)
    {
        var i = start;
        while (i < line.Length)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
            if (line[i] == target) return i;
            i++;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // Shared "pending symbol" bookkeeping
    // ------------------------------------------------------------------

    private sealed class PendingSymbol
    {
        public string Name = "";
        public SymbolKind Kind;
        public string QualifiedName = "";
        public string? ParentQualifiedName;
        public int HeaderStartLine;
        public int EndLine;
        public int IndentLevel;
    }

    private sealed record HeaderMatch(
        string Name,
        SymbolKind Kind,
        bool HasBody,
        IReadOnlyList<(string Target, RelationKind Kind)> Inheritance);

    // ------------------------------------------------------------------
    // Curly-brace languages: C#, TypeScript/JavaScript
    // ------------------------------------------------------------------

    private static (List<SymbolDraft> Symbols, List<RelationDraft> Relations) ExtractCurly(
        string[] lines, string[] cleanLines, CodeDialect dialect)
    {
        var symbols = new List<SymbolDraft>();
        var symbolCleanBodies = new List<string>();
        var relations = new List<RelationDraft>();

        var containerStack = new Stack<PendingSymbol>();
        var braceStack = new Stack<PendingSymbol?>();
        PendingSymbol? awaitingHeader = null;

        for (var li = 0; li < lines.Length; li++)
        {
            var lineNumber = li + 1;
            var cleanLine = cleanLines[li];
            var trimmed = cleanLine.TrimStart();

            if (awaitingHeader is null && trimmed.Length > 0)
            {
                var parent = containerStack.Count > 0 ? containerStack.Peek() : null;
                var match = dialect == CodeDialect.CSharp
                    ? TryMatchCSharpHeader(trimmed, parent)
                    : TryMatchTsHeader(trimmed, parent);

                if (match is { } m)
                {
                    var qualifiedName = parent is null ? m.Name : $"{parent.QualifiedName}.{m.Name}";

                    foreach (var (target, relKind) in m.Inheritance)
                        relations.Add(new RelationDraft(qualifiedName, target, relKind));

                    if (m.HasBody)
                    {
                        awaitingHeader = new PendingSymbol
                        {
                            Name = m.Name,
                            Kind = m.Kind,
                            QualifiedName = qualifiedName,
                            ParentQualifiedName = parent?.QualifiedName,
                            HeaderStartLine = lineNumber,
                        };
                    }
                    else
                    {
                        var doc = ExtractCStyleDocComment(lines, lineNumber);
                        symbols.Add(new SymbolDraft(
                            m.Name, qualifiedName, m.Kind, lineNumber, lineNumber,
                            lines[li].Trim(), string.Empty, doc, parent?.QualifiedName));
                        symbolCleanBodies.Add(string.Empty);
                    }
                }
            }

            foreach (var ch in cleanLine)
            {
                if (ch == '{')
                {
                    if (awaitingHeader is not null)
                    {
                        containerStack.Push(awaitingHeader);
                        braceStack.Push(awaitingHeader);
                        awaitingHeader = null;
                    }
                    else
                    {
                        braceStack.Push(null);
                    }
                }
                else if (ch == '}')
                {
                    if (braceStack.Count == 0) continue;
                    var popped = braceStack.Pop();
                    if (popped is null) continue;

                    popped.EndLine = lineNumber;
                    if (containerStack.Count > 0) containerStack.Pop();

                    var signature = lines[popped.HeaderStartLine - 1].Trim();
                    var body = string.Join('\n', lines[(popped.HeaderStartLine - 1)..popped.EndLine]);
                    var cleanBody = string.Join('\n', cleanLines[(popped.HeaderStartLine - 1)..popped.EndLine]);
                    var doc = ExtractCStyleDocComment(lines, popped.HeaderStartLine);

                    symbols.Add(new SymbolDraft(
                        popped.Name, popped.QualifiedName, popped.Kind,
                        popped.HeaderStartLine, popped.EndLine, signature, body, doc,
                        popped.ParentQualifiedName));
                    symbolCleanBodies.Add(cleanBody);
                }
            }
        }

        ExtractCallRelations(symbols, symbolCleanBodies, dialect, relations);
        return (symbols, relations);
    }

    // -- C# header matching -------------------------------------------------

    private static readonly Regex CsTypeRegex = new(
        @"^(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new)\s+)*" +
        @"(class|interface|struct|enum|record)(?:\s+(?:class|struct))?\s+(\w+)(?:<[^>{]*>)?(?:\s*\([^)]*\))?\s*(?::\s*([^\{;]+))?",
        RegexOptions.Compiled);

    private static readonly Regex CsMethodRegex = new(
        @"^(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|virtual|override|async|sealed|abstract|partial|new|unsafe|extern|readonly)\s+)+" +
        @"[\w<>\[\],\.\?]+\s+(\w+)\s*(?:<[^>{]*>)?\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex CsInterfaceMemberRegex = new(
        @"^[\w<>\[\],\.\?]+\s+(\w+)\s*(?:<[^>{]*>)?\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex CsConstructorRegex = new(
        @"^(?:(?:public|private|protected|internal|static)\s+)+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> CsReserved = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "checked",
        "unchecked", "return", "else", "do", "new", "base", "this", "throw", "yield", "typeof",
        "sizeof", "nameof", "default", "await",
    };

    private static bool IsCsReservedWord(string name) => CsReserved.Contains(name);

    private static HeaderMatch? TryMatchCSharpHeader(string line, PendingSymbol? parent)
    {
        var typeMatch = CsTypeRegex.Match(line);
        if (typeMatch.Success)
        {
            var keyword = typeMatch.Groups[1].Value;
            var name = typeMatch.Groups[2].Value;
            var kind = keyword switch
            {
                "interface" => SymbolKind.Interface,
                "struct" => SymbolKind.Struct,
                "enum" => SymbolKind.Enum,
                _ => SymbolKind.Class, // class, record
            };
            var inheritance = ParseCSharpInheritance(typeMatch.Groups[3].Value);
            var hasBody = !line.TrimEnd().EndsWith(';');
            return new HeaderMatch(name, kind, hasBody, inheritance);
        }

        if (parent is { Kind: SymbolKind.Interface })
        {
            var ifaceMatch = CsInterfaceMemberRegex.Match(line);
            if (ifaceMatch.Success && !IsCsReservedWord(ifaceMatch.Groups[1].Value))
            {
                var hasBody = !line.TrimEnd().EndsWith(';');
                return new HeaderMatch(ifaceMatch.Groups[1].Value, SymbolKind.Method, hasBody, []);
            }
        }

        var methodMatch = CsMethodRegex.Match(line);
        if (methodMatch.Success && !IsCsReservedWord(methodMatch.Groups[1].Value))
        {
            var hasBody = !line.TrimEnd().EndsWith(';');
            return new HeaderMatch(methodMatch.Groups[1].Value, SymbolKind.Method, hasBody, []);
        }

        if (parent is { Kind: SymbolKind.Class or SymbolKind.Struct })
        {
            var ctorMatch = CsConstructorRegex.Match(line);
            if (ctorMatch.Success && ctorMatch.Groups[1].Value == parent.Name)
            {
                var hasBody = !line.TrimEnd().EndsWith(';');
                return new HeaderMatch(ctorMatch.Groups[1].Value, SymbolKind.Method, hasBody, []);
            }
        }

        return null;
    }

    private static List<(string, RelationKind)> ParseCSharpInheritance(string? listText)
    {
        var result = new List<(string, RelationKind)>();
        if (string.IsNullOrWhiteSpace(listText)) return result;

        var stripped = Regex.Replace(listText, "<[^>]*>", "");
        foreach (var raw in stripped.Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (name.Contains(' ')) name = name.Split(' ')[0]; // drop trailing "where T : ..." fragments
            if (name.Length == 0) continue;

            var kind = name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1])
                ? RelationKind.Implements
                : RelationKind.Extends;
            result.Add((name, kind));
        }

        return result;
    }

    // -- TypeScript / JavaScript header matching -----------------------------

    private static readonly Regex TsTypeRegex = new(
        @"^(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(class|interface)\s+(\w+)(?:<[^>{]*>)?" +
        @"(?:\s+extends\s+([\w\.<>\[\], ]+?)(?=\s*(?:implements\b|\{|$)))?" +
        @"(?:\s+implements\s+([\w\.<>,\[\] ]+?)(?=\s*\{?\s*$))?",
        RegexOptions.Compiled);

    private static readonly Regex TsFunctionRegex = new(
        @"^(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s+(\w+)\s*(?:<[^>{]*>)?\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex TsArrowFunctionRegex = new(
        @"^(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::\s*[^=]+)?=\s*(?:async\s*)?\(([^)]*)\)\s*(?::\s*[^=]+)?=>",
        RegexOptions.Compiled);

    private static readonly Regex TsMethodRegex = new(
        @"^(?:public\s+|private\s+|protected\s+|static\s+|async\s+|readonly\s+|abstract\s+|override\s+|get\s+|set\s+)*" +
        @"\*?\s*([A-Za-z_$][\w$]*)\s*(?:<[^>{]*>)?\s*\(([^)]*)\)\s*(?::\s*[^\{;=]+)?",
        RegexOptions.Compiled);

    private static readonly HashSet<string> JsReserved = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "function", "return", "else", "do", "new",
        "typeof", "await", "yield", "super", "this", "in", "of", "instanceof", "delete", "void", "throw",
    };

    private static bool IsJsReservedWord(string name) => JsReserved.Contains(name);

    private static HeaderMatch? TryMatchTsHeader(string line, PendingSymbol? parent)
    {
        var typeMatch = TsTypeRegex.Match(line);
        if (typeMatch.Success)
        {
            var kind = typeMatch.Groups[1].Value == "interface" ? SymbolKind.Interface : SymbolKind.Class;
            var name = typeMatch.Groups[2].Value;

            var inheritance = new List<(string, RelationKind)>();
            if (typeMatch.Groups[3].Success)
                foreach (var t in SplitTsList(typeMatch.Groups[3].Value))
                    inheritance.Add((t, RelationKind.Extends));
            if (typeMatch.Groups[4].Success)
                foreach (var t in SplitTsList(typeMatch.Groups[4].Value))
                    inheritance.Add((t, RelationKind.Implements));

            return new HeaderMatch(name, kind, true, inheritance);
        }

        var funcMatch = TsFunctionRegex.Match(line);
        if (funcMatch.Success && !IsJsReservedWord(funcMatch.Groups[1].Value))
            return new HeaderMatch(funcMatch.Groups[1].Value, SymbolKind.Function, true, []);

        var arrowMatch = TsArrowFunctionRegex.Match(line);
        if (arrowMatch.Success && !IsJsReservedWord(arrowMatch.Groups[1].Value))
            return new HeaderMatch(arrowMatch.Groups[1].Value, SymbolKind.Function, true, []);

        if (parent is { Kind: SymbolKind.Class or SymbolKind.Interface })
        {
            var methodMatch = TsMethodRegex.Match(line);
            if (methodMatch.Success && !IsJsReservedWord(methodMatch.Groups[1].Value))
            {
                var hasBody = !line.TrimEnd().EndsWith(';');
                return new HeaderMatch(methodMatch.Groups[1].Value, SymbolKind.Method, hasBody, []);
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitTsList(string text) =>
        Regex.Replace(text, "<[^>]*>", "")
            .Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0);

    // ------------------------------------------------------------------
    // Python (indentation-based)
    // ------------------------------------------------------------------

    private static readonly Regex PyDefRegex = new(
        @"^\s*(?:async\s+)?def\s+(\w+)\s*\(([^)]*)\)\s*(?:->\s*[^:]+)?:",
        RegexOptions.Compiled);

    private static readonly Regex PyClassRegex = new(
        @"^\s*class\s+(\w+)\s*(?:\(([^)]*)\))?\s*:",
        RegexOptions.Compiled);

    private static (List<SymbolDraft> Symbols, List<RelationDraft> Relations) ExtractPython(
        string[] lines, string[] cleanLines)
    {
        var symbols = new List<SymbolDraft>();
        var symbolCleanBodies = new List<string>();
        var relations = new List<RelationDraft>();
        var stack = new Stack<PendingSymbol>();
        var lastContentLine = 0;

        void CloseDownTo(int indent, int atLine)
        {
            while (stack.Count > 0 && indent <= stack.Peek().IndentLevel)
            {
                var popped = stack.Pop();
                popped.EndLine = atLine;

                var signature = lines[popped.HeaderStartLine - 1].Trim();
                var body = string.Join('\n', lines[(popped.HeaderStartLine - 1)..popped.EndLine]);
                var cleanBody = string.Join('\n', cleanLines[(popped.HeaderStartLine - 1)..popped.EndLine]);
                var doc = ExtractPythonDocComment(lines, popped.HeaderStartLine, popped.EndLine);

                symbols.Add(new SymbolDraft(
                    popped.Name, popped.QualifiedName, popped.Kind,
                    popped.HeaderStartLine, popped.EndLine, signature, body, doc, popped.ParentQualifiedName));
                symbolCleanBodies.Add(cleanBody);
            }
        }

        for (var li = 0; li < lines.Length; li++)
        {
            var lineNumber = li + 1;
            var cleanLine = cleanLines[li];
            if (cleanLine.Trim().Length == 0) continue; // blank / fully-commented line: scope-neutral

            var indent = IndentOf(cleanLine);
            CloseDownTo(indent, lastContentLine == 0 ? lineNumber : lastContentLine);
            lastContentLine = lineNumber;

            var defMatch = PyDefRegex.Match(cleanLine);
            if (defMatch.Success)
            {
                var parent = stack.Count > 0 ? stack.Peek() : null;
                var name = defMatch.Groups[1].Value;
                var kind = parent is { Kind: SymbolKind.Class } ? SymbolKind.Method : SymbolKind.Function;
                var qualifiedName = parent is null ? name : $"{parent.QualifiedName}.{name}";
                stack.Push(new PendingSymbol
                {
                    Name = name,
                    Kind = kind,
                    QualifiedName = qualifiedName,
                    ParentQualifiedName = parent?.QualifiedName,
                    HeaderStartLine = lineNumber,
                    IndentLevel = indent,
                });
                continue;
            }

            var classMatch = PyClassRegex.Match(cleanLine);
            if (classMatch.Success)
            {
                var parent = stack.Count > 0 ? stack.Peek() : null;
                var name = classMatch.Groups[1].Value;
                var qualifiedName = parent is null ? name : $"{parent.QualifiedName}.{name}";

                foreach (var baseName in SplitPythonBases(classMatch.Groups[2].Value))
                    relations.Add(new RelationDraft(qualifiedName, baseName, RelationKind.Extends));

                stack.Push(new PendingSymbol
                {
                    Name = name,
                    Kind = SymbolKind.Class,
                    QualifiedName = qualifiedName,
                    ParentQualifiedName = parent?.QualifiedName,
                    HeaderStartLine = lineNumber,
                    IndentLevel = indent,
                });
            }
        }

        CloseDownTo(-1, lastContentLine == 0 ? lines.Length : lastContentLine);

        ExtractCallRelations(symbols, symbolCleanBodies, CodeDialect.Python, relations);
        return (symbols, relations);
    }

    private static int IndentOf(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == ' ') count++;
            else if (c == '\t') count += 8;
            else break;
        }
        return count;
    }

    private static IEnumerable<string> SplitPythonBases(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Split(','))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.Contains('=')) continue; // skip kwargs like metaclass=...
            yield return t;
        }
    }

    private static string? ExtractPythonDocComment(string[] lines, int headerLine, int endLine)
    {
        // headerLine is 1-based; lines[headerLine] (0-based) is the first line of the body.
        for (var i = headerLine; i < endLine && i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0) continue;

            if (text.StartsWith("\"\"\"", StringComparison.Ordinal) || text.StartsWith("'''", StringComparison.Ordinal))
            {
                var quote = text[..3];
                var rest = text[3..];
                var closeIdx = rest.IndexOf(quote, StringComparison.Ordinal);
                if (closeIdx >= 0) return rest[..closeIdx].Trim();

                var sb = new StringBuilder(rest);
                for (var j = i + 1; j < endLine && j < lines.Length; j++)
                {
                    var next = lines[j];
                    var idx = next.IndexOf(quote, StringComparison.Ordinal);
                    if (idx >= 0) { sb.Append('\n').Append(next[..idx]); break; }
                    sb.Append('\n').Append(next);
                }
                return sb.ToString().Trim();
            }

            return null; // first statement in the body isn't a docstring
        }
        return null;
    }

    private static string? ExtractCStyleDocComment(string[] lines, int headerLine)
    {
        var idx = headerLine - 2; // 0-based index of the line immediately above the header
        while (idx >= 0 && lines[idx].Trim().Length == 0) idx--;
        if (idx < 0) return null;

        if (lines[idx].Trim().EndsWith("*/", StringComparison.Ordinal))
        {
            var end = idx;
            var start = idx;
            while (start >= 0 && !lines[start].Contains("/*", StringComparison.Ordinal)) start--;
            if (start >= 0)
                return string.Join('\n', lines[start..(end + 1)]).Trim();
        }

        if (lines[idx].TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            var end = idx;
            var start = idx;
            while (start - 1 >= 0 && lines[start - 1].TrimStart().StartsWith("///", StringComparison.Ordinal)) start--;
            return string.Join('\n', lines[start..(end + 1)].Select(l => l.TrimStart().TrimStart('/').Trim())).Trim();
        }

        return null;
    }

    // ------------------------------------------------------------------
    // "Calls" relation extraction — shared across all dialects.
    // ------------------------------------------------------------------

    private static readonly Regex CallRegex = new(@"\b([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> PyReserved = new(StringComparer.Ordinal)
    {
        "if", "elif", "for", "while", "def", "class", "return", "with", "except", "lambda",
        "yield", "assert", "raise", "print", "super",
    };

    private static void ExtractCallRelations(
        List<SymbolDraft> symbols, List<string> cleanBodies, CodeDialect dialect, List<RelationDraft> relations)
    {
        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in symbols)
            if (s.Kind is SymbolKind.Function or SymbolKind.Method)
                knownNames.Add(s.Name);

        if (knownNames.Count == 0) return;

        var reserved = dialect switch
        {
            CodeDialect.CSharp => CsReserved,
            CodeDialect.Python => PyReserved,
            _ => JsReserved,
        };

        for (var i = 0; i < symbols.Count; i++)
        {
            var body = cleanBodies[i];
            if (body.Length == 0) continue;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in CallRegex.Matches(body))
            {
                var callee = m.Groups[1].Value;
                if (!knownNames.Contains(callee)) continue;
                if (reserved.Contains(callee)) continue;
                if (!seen.Add(callee)) continue;
                relations.Add(new RelationDraft(symbols[i].QualifiedName, callee, RelationKind.Calls));
            }
        }
    }
}
