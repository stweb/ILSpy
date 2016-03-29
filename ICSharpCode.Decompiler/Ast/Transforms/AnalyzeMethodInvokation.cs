using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ICSharpCode.NRefactory.CSharp;
using Mono.Cecil;

namespace ICSharpCode.Decompiler.Ast.Transforms
{
	/// <summary>
	/// Finds all method invocations and creates statistics
	/// orginally built to identify legacy methods to be transformed 
	/// by sweber
	/// </summary>
	public class AnalyzeMethodInvokation
	{
        private readonly Dictionary<string,int> _stats = new Dictionary<string,int>(); 
	
		public void Run(AstNode compilationUnit)
		{
		    foreach (InvocationExpression invocation in compilationUnit.Descendants.OfType<InvocationExpression>())
		    {
		        var mre = invocation.Target as MemberReferenceExpression;
		        var methodReference = invocation.Annotation<MethodReference>();
		        if (mre != null && mre.Target is TypeReferenceExpression && methodReference != null)
		        {
		            var name = mre.ToString();
		            int count;
		            _stats[name] = _stats.TryGetValue(name, out count) ? count + 1 : 1;
		        }
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

            names = DecompilerContext.EliminatedProps.Keys.ToList();
            names.Sort();
            foreach (var key in names)
            {
                var line = string.Format("{0} => {1}\n", key, DecompilerContext.EliminatedProps[key]);
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
