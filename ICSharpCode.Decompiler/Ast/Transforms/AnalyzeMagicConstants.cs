using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.NRefactory.CSharp;

namespace ICSharpCode.Decompiler.Ast.Transforms
{
	/// <summary>
	/// Finds all 'magic' constants and creates statistics
	/// by sweber
	/// </summary>
	public class AnalyzeMagicConstants
	{
        private readonly Dictionary<string,int> _stats = new Dictionary<string,int>();
	    private readonly CodeDomProvider _provider = CodeDomProvider.CreateProvider("CSharp");

        private string ToLiteral(string input)
        {
            using (var writer = new StringWriter())
            {
                _provider.GenerateCodeFromExpression(new CodePrimitiveExpression(input), writer, null);
                return writer.ToString();
            }
        }
	
		public void Run(AstNode compilationUnit)
		{
            foreach (PrimitiveExpression prim in compilationUnit.Descendants.OfType<PrimitiveExpression>())
            {
                if (prim.Parent is EnumMemberDeclaration) continue;

                var val = prim.Value;
                if (val is bool) continue; // skip all boolean

                if (val.Equals(0) || val.Equals(1) || val.Equals(-1) || val.Equals(0.0)) continue; // typical consts

                var name = '(' + val.GetType().Name + "): " + val.ToString();

                var str = val as string;
                if (str != null)
                {
                    if (str == "") continue; // String.empty -> replace?

                    //if (str.Length <= 1) continue; 

                    if (str.IndexOf('%') >= 0 || str.Contains("{0}")) continue; // skip format strings

                    if (str.IndexOf(' ') >= 0) continue; // skip 'text'

                    name = "(string): " + ToLiteral(str);
                }

		        int count;
		        _stats[name] = _stats.TryGetValue(name, out count) ? count + 1 : 1;		        
		    }
		}

        public void MergeStats(ConcurrentDictionary<string, int> dict)
	    {
	        var names = _stats.Keys.ToList();
	        names.Sort();

	        // merge dict
	        foreach (var name in names)
	        {
	            dict.AddOrUpdate(name, _stats[name], (s, i) => i + _stats[name]);
	        }

            _stats.Clear();
	    }

	    public static void Output(IDictionary<string, int> stats, ITextOutput output)
	    {
            var names = stats.Keys.ToList();
            names.Sort();
            foreach (var key in names)
	        {
                var line = string.Format("{0}\t{1}\n", stats[key], key);
                output.Write(line);
	        }
	    }

	    public static string Report(IDictionary<string, int> stats)
	    {
	        var sb = new StringBuilder();
	        var names = stats.Keys.ToList();
	        names.Sort();
	        foreach (var key in names)
	        {
	            sb.Append(key);
	            sb.Append(";");
	            sb.Append(stats[key]);
	            sb.AppendLine();
	        }
	        return sb.ToString();
	    }
	}
}
