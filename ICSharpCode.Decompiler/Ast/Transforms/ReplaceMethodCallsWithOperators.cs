// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ICSharpCode.NRefactory.PatternMatching;
using Mono.Cecil;
using Ast = ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp;
using Expression = ICSharpCode.NRefactory.CSharp.Expression;
using ExpressionStatement = ICSharpCode.NRefactory.CSharp.ExpressionStatement;
using InvocationExpression = ICSharpCode.NRefactory.CSharp.InvocationExpression;

namespace ICSharpCode.Decompiler.Ast.Transforms
{
	/// <summary>
	/// Replaces method calls with the appropriate operator expressions.
	/// Also simplifies "x = x op y" into "x op= y" where possible.
	/// </summary>
	public class ReplaceMethodCallsWithOperators : DepthFirstAstVisitor<object, object>, IAstTransform
	{
		static readonly MemberReferenceExpression typeHandleOnTypeOfPattern = new MemberReferenceExpression {
			Target = new Choice {
				new TypeOfExpression(new AnyNode()),
				new UndocumentedExpression { UndocumentedExpressionType = UndocumentedExpressionType.RefType, Arguments = { new AnyNode() } }
			},
			MemberName = "TypeHandle"
		};

	    readonly DecompilerContext context;
		
		public ReplaceMethodCallsWithOperators(DecompilerContext context)
		{
			this.context = context;
		}
		
		public override object VisitInvocationExpression(InvocationExpression invocationExpression, object data)
		{
			base.VisitInvocationExpression(invocationExpression, data);
			ProcessInvocationExpression(invocationExpression);
			return null;
		}

	    #region Helpers

        private static Expression Invoke(string typeName, string method, IEnumerable<Expression> param, bool istrue = true)
	    {
            var ex = new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType(typeName)), method), param);
            return istrue ? ex : (Expression)new UnaryOperatorExpression(UnaryOperatorType.Not, ex);
	    }

        private static Expression Invoke1(Expression fst, string method, Expression snd, bool istrue = true)
        {
            var ex = new InvocationExpression(new MemberReferenceExpression(fst, method), snd);
            return istrue ? ex : (Expression)new UnaryOperatorExpression(UnaryOperatorType.Not, ex);
        }

	    private static bool NullOrEmpty(Expression expr)
	    {
	        return (expr is NullReferenceExpression) ||
	               (expr.ToString().Equals("\"\"") || // TODO replace string based check
	                expr.ToString().Equals("Variants.Null ()"));
	    }


        private static InvocationExpression InvokeEquals(Expression[] arguments)
        {
            var cmp = new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("StringComparison")), "OrdinalIgnoreCase");
            return arguments[0] is PrimitiveExpression
                ? new InvocationExpression(new MemberReferenceExpression(arguments[1], "Equals"),
                    new[] {arguments[0], cmp})
                : new InvocationExpression(new MemberReferenceExpression(arguments[0], "Equals"),
                    new[] {arguments[1], cmp});
        }

	    // decrement value by 1 - used to adjust Delphi string indexes
	    private static Expression Decrement(Expression expr)
	    {
	        var prim = expr as PrimitiveExpression;
	        if (prim != null && prim.Value is int)
	        {
	            return new PrimitiveExpression((int) prim.Value - 1);
	        }

	        var bin = expr as BinaryOperatorExpression;
	        if (bin != null && bin.Right is PrimitiveExpression && ((PrimitiveExpression) bin.Right).Value.ToString() == "1") // todo don't use string comparison
	        {
	            return bin.Left.Detach();
	        }

	        //return new UnaryOperatorExpression(UnaryOperatorType.Decrement, expr);
            return new BinaryOperatorExpression(expr, BinaryOperatorType.Subtract, new PrimitiveExpression(1));
	    }
        
	    #endregion


	    internal static void ProcessInvocationExpression(InvocationExpression invocationExpression)
		{
			MethodReference methodRef = invocationExpression.Annotation<MethodReference>();
	        if (methodRef == null)
	        {
	            var ie = invocationExpression.Target as IdentifierExpression;
	            if (ie != null && ie.Identifier == "ckfinite")
	            {
	                var arg = invocationExpression.Arguments.FirstOrNullObject();
                    if (arg != null)
                        invocationExpression.ReplaceWith(arg);
	            }
	            return;
	        }
	        var arguments = invocationExpression.Arguments.ToArray();
			
			// Reduce "String.Concat(a, b)" to "a + b"
	        if (methodRef.Name == "Concat" && methodRef.DeclaringType.FullName == "System.String")
	        {
	            if (arguments.Length >= 2)
	            {
	                invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
	                Expression expr = arguments[0];
	                for (int i = 1; i < arguments.Length; i++)
	                {
	                    expr = new BinaryOperatorExpression(expr, BinaryOperatorType.Add, arguments[i]);
	                }
	                invocationExpression.ReplaceWith(expr);
	                return;
	            }
                if (arguments.Length == 1 && arguments[0] is ArrayCreateExpression) // new string[] (Delphi)
	            {
                    invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                    var arg = arguments[0] as ArrayCreateExpression;
	                var strings = arg.Initializer.Elements.ToArray();
                    Expression expr = strings[0].Detach();
                    for (int i = 1; i < strings.Length; i++)
                    {
                        expr = new BinaryOperatorExpression(expr, BinaryOperatorType.Add, strings[i].Detach());
                    }
                    invocationExpression.ReplaceWith(expr);
                    return;
                }
	        }

            // Delphi indexed properties
            if (methodRef.Parameters.Count > 1)
            {
                // argument might be value of a setter
                if (methodRef.Name.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
                {
                    string key = methodRef.DeclaringType.FullName + "." + methodRef.Name;
                    string mn;
                    if (!DecompilerContext.EliminatedProps.TryGetValue(key, out mn))
                    {
                        mn = "Set" + methodRef.Name.Substring(4).TrimEnd('s');
                        Trace.TraceWarning(key + " ~?> " + mn);
                    }
                    else
                    {
                        invocationExpression.Arguments.Clear();
                        var mre = invocationExpression.Target as MemberReferenceExpression;
                        var inv = new InvocationExpression(new MemberReferenceExpression(mre.Target.Detach(), mn),
                            arguments);
                        invocationExpression.ReplaceWith(inv);                       
                    }
                    return;
                }
            }
            else if (methodRef.Parameters.Count > 0)
            {
                // argument might be value of a setter
                if (methodRef.Name.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                {
                    string key = methodRef.DeclaringType.FullName + "." + methodRef.Name;
                    string mn;
                    if (DecompilerContext.EliminatedProps.TryGetValue(key, out mn))
                    {
                        invocationExpression.Arguments.Clear();
                        var mre = invocationExpression.Target as MemberReferenceExpression;
                        var inv = new InvocationExpression(new MemberReferenceExpression(mre.Target.Detach(), mn),
                            arguments);
                        invocationExpression.ReplaceWith(inv);
                    }
                    else
                    {
                        mn = "Get" + methodRef.Name.Substring(4).TrimEnd('s');
                        Trace.TraceWarning(key + " ~?> " + mn);
                    }
                    return;
                }
            }


	        #region DELPHI.net 2007 support 

            if (methodRef.DeclaringType.FullName == "Borland.Vcl.Units.SysUtils")
            {
                if (methodRef.Name == "Format")
                {
                    if (arguments.Length == 2 &&  (arguments[0] is PrimitiveExpression) && arguments[1] is ArrayCreateExpression) // new object[] 
                    try
                    {
                        var fmte = (arguments[0] as PrimitiveExpression).Value as string;
                        var fmt = fmte.Replace("%s", "{0}").Replace("%d", "{0}"); // TODO
                        invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                        var arg = arguments[1] as ArrayCreateExpression;
                        List<Expression> items = arg.Initializer.Elements.ToList();
                        items.ForEach(i => i.Detach());
                        items.Insert(0, new PrimitiveExpression(fmt));
                        invocationExpression.ReplaceWith(
                            new InvocationExpression(
                                new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("String")),
                                                              "Format"), items.ToArray()));
                        return;
                    }
                    catch (Exception e)
                    {
                        Trace.TraceError(e.Message);
                    }
                }
                if ((methodRef.Name == "CompareText" || methodRef.Name == "AnsiCompareText") && arguments.Length == 2)
                {
                    var parent = invocationExpression.Parent as BinaryOperatorExpression;
                    if (parent == null) return;

                    if (!(parent.Operator == BinaryOperatorType.InEquality || parent.Operator == BinaryOperatorType.Equality))
                        return;

                    var right = parent.Right as PrimitiveExpression;
                    if (right == null || !right.Value.Equals(0))
                        return;
                    
                    invocationExpression.Arguments.Clear();
                    parent.ReplaceWith(InvokeEquals(arguments));
                    return;
                }
                if ((methodRef.Name == "SameText" || methodRef.Name == "AnsiSameText") && arguments.Length == 2)
                {
                    invocationExpression.Arguments.Clear();
                    invocationExpression.ReplaceWith(InvokeEquals(arguments));
                    return;
                }

                Expression nexpr = null;
                switch (methodRef.Name)
                {
                    case "FloatToStr":
                    case "IntToStr":
                        // TODO maybe use Convert.ToString() to bypass potential override?
                        nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), "ToString"));
                        break;
                    case "AnsiUpperCase":
                    case "UpperCase":
                        nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), "ToUpper"));
                        break;
                    case "AnsiLowerCase":
                    case "LowerCase": 
                        nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), "ToLower"));
                        break;
                    case "Trim": 
                        nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), "Trim"));
                        break;
                    //case "StrReplace":
                    //    nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), ""));
                    //    break;
                }
                if (nexpr != null)
                {
                    invocationExpression.Arguments.Clear();
                    invocationExpression.ReplaceWith(nexpr);
                    return;
                }
            }
	        //if (methodRef.DeclaringType.FullName == "Borland.Delphi.Units.System")
            //{
            //    if (methodRef.Name == "WideStringReplace" && arguments.Length == 2)
            //    {
            //        // 				result = WideStrUtils.WideStringReplace(result, "<%GEN_VAT_OR_TAX%>", "VAT", TReplaceFlags.rfReplaceAll | TReplaceFlags.rfIgnoreCase);

            //    }
            //}
	        // Reduce 'System.@WStrCmp(l, "") != 0' to !String.IsNullOrEmpty(l)
            // Reduce 'System.@WStrCmp(l, "") > 0'  to String.IsNullOrEmpty(l)
	        if (methodRef.DeclaringType.FullName == "Borland.Delphi.Units.System")
	        {
	            if (methodRef.Name == "@WStrCmp" && arguments.Length == 2)
	            {
	                var parent = invocationExpression.Parent as BinaryOperatorExpression;
	                if (parent == null) return;

	                if (!(parent.Operator == BinaryOperatorType.InEquality ||
	                      parent.Operator == BinaryOperatorType.Equality ||
	                      parent.Operator == BinaryOperatorType.GreaterThan))
	                    return;

	                var right = parent.Right as PrimitiveExpression;
	                if (right == null || !right.Value.Equals(0))
	                    return;

	                invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
	                Expression nexpr = null;
                    Expression fst = arguments[0];
                    Expression snd = arguments[1];
	                var nullOrEmpty = NullOrEmpty(snd);
	                if (!nullOrEmpty && NullOrEmpty(fst))
	                {
                        snd = arguments[0]; // swap args so that null is on the right
                        fst = arguments[1];
                        nullOrEmpty = true;
	                }

	                if (parent.Operator == BinaryOperatorType.Equality)
	                {
                        // {System.WStrCmp () == 0}
	                    nexpr = nullOrEmpty
	                        ? Invoke("String", "IsNullOrEmpty", new[] {fst})
	                        : //  Invoke1(fst, "Equals", snd);
	                          new BinaryOperatorExpression(fst, BinaryOperatorType.Equality, snd);
	                }
	                else if (parent.Operator == BinaryOperatorType.InEquality)
	                {
                        // {System.WStrCmp () != 0}
	                    nexpr = nullOrEmpty ? Invoke("String", "IsNullOrEmpty", new[] {fst}, false) : Invoke1(fst, "Equals", snd, false);
	                }
	                else if (parent.Operator == BinaryOperatorType.GreaterThan)
	                {
	                    nexpr = Invoke1(fst, "Equals", snd, false); // TODO check case
	                }
	                else
	                {
	                    throw new Exception("unhandled");
	                }

	                if (nexpr != null)
	                {
	                    parent.ReplaceWith(nexpr);
	                    return;
	                }
	            }
	            if (methodRef.Name == "@WStrCopy" && arguments.Length == 3)
	            {
	                invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
	                invocationExpression.ReplaceWith(
	                    new InvocationExpression(new MemberReferenceExpression(arguments[0], "Substring"),
	                        new[] {Decrement(arguments[1]), arguments[2]})); // index, length, TODO arg2 must be < length
	                return;
	            }

                if (arguments.Length == 1 && 
                   (methodRef.Name == "@WStrFromWChar" || methodRef.Name == "@WStrFromLStr" || methodRef.Name == "@LStrFromWStr"))
	            {
                    invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                    invocationExpression.ReplaceWith(arguments[0]); // replace call with argument
                    return;
	            }
                if (methodRef.Name == "@WStrFromWChar" && arguments.Length == 1)
                {
                    invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                    invocationExpression.ReplaceWith(arguments[0]); // replace call with argument
                    return;
                }
                if ((methodRef.Name == "Pos" || methodRef.Name == "AnsiPos") && arguments.Length == 2)
                {
                    var parent = invocationExpression.Parent as BinaryOperatorExpression;
                    if (parent != null && (parent.Operator == BinaryOperatorType.GreaterThan))
                    {
                        var right = parent.Right as PrimitiveExpression;
                        if (right != null && right.Value.Equals(0))
                        {
                            invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                            parent.ReplaceWith(new InvocationExpression(new MemberReferenceExpression(arguments[1], "Contains"), new[] { arguments[0] }));
                            return;
                        }                        
                    }

                    invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                    invocationExpression.ReplaceWith(new BinaryOperatorExpression(
                        new InvocationExpression(new MemberReferenceExpression(arguments[1], "IndexOf"), new[] { arguments[0] }), 
                        BinaryOperatorType.Add, new PrimitiveExpression(1)));
                    return;
                }
	            if (methodRef.Name == "@Assert")
	            {
                    //invocationExpression.Remove();
                    invocationExpression.Arguments.Clear();
	                invocationExpression.Parent.ReplaceWith(new ThrowStatement(new ObjectCreateExpression(new SimpleType("Exception"), arguments[0]))); // TODO
                    
	            }
            }
		    if (methodRef.DeclaringType.FullName.StartsWith("Borland.Vcl.Units"))
            {
                Expression nexpr = null;
                switch (methodRef.Name)
                {
                    case "VarToStr": // Variants
                        // TODO maybe use Convert.ToString() to bypass potential override?
                        nexpr = new InvocationExpression(new MemberReferenceExpression(arguments[0].Detach(), "ToString"));
                        break;
                    case "Null": // Variants
                        nexpr = new NullReferenceExpression();
                        break;
                    case "VarIsNull": // Variants
                        nexpr = new BinaryOperatorExpression(arguments[0].Detach(), BinaryOperatorType.Equality, new NullReferenceExpression());
                        break;
                }
                if (nexpr != null)
                {
                    invocationExpression.Arguments.Clear();
                    invocationExpression.ReplaceWith(nexpr);
                    return;
                }
            }
            if ( methodRef.Name == "e5Log" || methodRef.Name == "e5Warn" || methodRef.Name == "MisLog" || methodRef.Name == "MisLogFormatted")
            {
                invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                var l = arguments.Length;
                var lt = "Log";
                var lastarg = arguments.Last() as MemberReferenceExpression;
                if (lastarg != null)
                {
                    lt = lastarg.ToString().Substring(3);
                    l--;
                }
                var newargs = arguments.Skip(1).Take(l-1).ToArray();
                invocationExpression.ReplaceWith(new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("Logger")), lt), newargs));
                return;
            }
	        if (methodRef.DeclaringType.FullName.Equals("ShareIt.Units.dbEnums"))
	        {
	            if (methodRef.Name.EndsWith("ToStr"))
	            {   // use public static string EnumExtensions.GetID(Enum value)
                    invocationExpression.Arguments.Clear();
                    invocationExpression.ReplaceWith(new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("EnumExtensions")), "GetID"), arguments));
                    return;
	            }  
                if (methodRef.Name.StartsWith("StrTo"))
	            {   // EnumExtensions.GetEnum<TCurrencyEnum>("EUR")
	                var ety = "T" + methodRef.Name.Substring(5) + "Enum";
                    invocationExpression.Arguments.Clear();
                    invocationExpression.ReplaceWith(new InvocationExpression(new MemberReferenceExpression(new TypeReferenceExpression(new SimpleType("EnumExtensions")), "GetEnum<" + ety + ">"), arguments));
                    return;
	            }
	        }
	        if (methodRef.Name == "FreeAndNil" || methodRef.Name == "@GetMetaFromObject" || methodRef.Name == "RunClassConstructor" || methodRef.Name == "@AddFinalization" || methodRef.Name == "GetMetaFromHandle") // System.GetMetaFromObject
            {
                invocationExpression.Remove(); //placeWith(new NullReferenceExpression());
                return;
	        }
            if (methodRef.Name == "ClassName" )
            {
                if (arguments.Length == 1)
                {
                    string cname;
                    var ase = arguments[0] as AsExpression;
                    if (ase != null)
                        cname = ase.Type.ToString();
                    else
                    {
                        cname = arguments[0].ToString();
                    }
                    

                    invocationExpression.ReplaceWith(new PrimitiveExpression(StrBefore(cname, '.')));
                        // TODO inaccurate " as ..."  or this.GetType().Name;
                }
                else
                    Trace.TraceInformation("ClassName " + invocationExpression.ToString());
            }
            if (methodRef.Name == "CreateFmt") 
            {
                var mre = invocationExpression.Target as MemberReferenceExpression;
                var tre = mre.Target as TypeReferenceExpression; // handle ExceptionHelper -> Exception

                var fmte = (arguments[1] as PrimitiveExpression).Value as string;
                var fmt = fmte.Replace("%s", "{0}").Replace("%d", "{0}"); // TODO
                invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
                var arg = arguments[2] as ArrayCreateExpression;
                List<Expression> items = arg.Initializer.Elements.ToList();
                items.ForEach(i => i.Detach());
                items.Insert(0, new PrimitiveExpression(fmt));

                var nex = new ObjectCreateExpression(tre.Type.Detach(), new[] { Invoke("String", "Format", items.ToArray()) });
                invocationExpression.ReplaceWith(nex);
                return;
            }
	        if (methodRef.Name == "Field")
	        {
	            var parent = invocationExpression.Parent;
                Trace.TraceInformation("FIELD " + parent.ToString());
	        }

	        #endregion

			switch (methodRef.FullName) {
				case "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)":
					if (arguments.Length == 1) {
						if (typeHandleOnTypeOfPattern.IsMatch(arguments[0])) {
							invocationExpression.ReplaceWith(((MemberReferenceExpression)arguments[0]).Target);
							return;
						}
					}
					break;
				case "System.Reflection.FieldInfo System.Reflection.FieldInfo::GetFieldFromHandle(System.RuntimeFieldHandle)":
					if (arguments.Length == 1) {
						MemberReferenceExpression mre = arguments[0] as MemberReferenceExpression;
						if (mre != null && mre.MemberName == "FieldHandle" && mre.Target.Annotation<LdTokenAnnotation>() != null) {
							invocationExpression.ReplaceWith(mre.Target);
							return;
						}
					}
					break;
				case "System.Reflection.FieldInfo System.Reflection.FieldInfo::GetFieldFromHandle(System.RuntimeFieldHandle,System.RuntimeTypeHandle)":
					if (arguments.Length == 2) {
						MemberReferenceExpression mre1 = arguments[0] as MemberReferenceExpression;
						MemberReferenceExpression mre2 = arguments[1] as MemberReferenceExpression;
						if (mre1 != null && mre1.MemberName == "FieldHandle" && mre1.Target.Annotation<LdTokenAnnotation>() != null) {
							if (mre2 != null && mre2.MemberName == "TypeHandle" && mre2.Target is TypeOfExpression) {
								Expression oldArg = ((InvocationExpression)mre1.Target).Arguments.Single();
								FieldReference field = oldArg.Annotation<FieldReference>();
								if (field != null) {
									AstType declaringType = ((TypeOfExpression)mre2.Target).Type.Detach();
									oldArg.ReplaceWith(declaringType.Member(field.Name).WithAnnotation(field));
									invocationExpression.ReplaceWith(mre1.Target);
									return;
								}
							}
						}
					}
					break;
			}
			
			BinaryOperatorType? bop = GetBinaryOperatorTypeFromMetadataName(methodRef.Name);
			if (bop != null && arguments.Length == 2) {
				invocationExpression.Arguments.Clear(); // detach arguments from invocationExpression
				invocationExpression.ReplaceWith(
					new BinaryOperatorExpression(arguments[0], bop.Value, arguments[1]).WithAnnotation(methodRef)
				);
				return;
			}
			UnaryOperatorType? uop = GetUnaryOperatorTypeFromMetadataName(methodRef.Name);
			if (uop != null && arguments.Length == 1) {
				arguments[0].Remove(); // detach argument
				invocationExpression.ReplaceWith(
					new UnaryOperatorExpression(uop.Value, arguments[0]).WithAnnotation(methodRef)
				);
				return;
			}
			if (methodRef.Name == "op_Explicit" && arguments.Length == 1) {
				arguments[0].Remove(); // detach argument
				invocationExpression.ReplaceWith(
					arguments[0].CastTo(AstBuilder.ConvertType(methodRef.ReturnType, methodRef.MethodReturnType))
					.WithAnnotation(methodRef)
				);
				return;
			}
			if (methodRef.Name == "op_Implicit" && arguments.Length == 1) {
				invocationExpression.ReplaceWith(arguments[0]);
				return;
			}
			if (methodRef.Name == "op_True" && arguments.Length == 1 && invocationExpression.Role == Roles.Condition) {
				invocationExpression.ReplaceWith(arguments[0]);
				return;
			}

			return;
		}

	    private static object StrBefore(string str, char ch)
        {
            var pos = str.IndexOf(ch);
            // return empty when ch not in string
            return pos > 0 ? str.Substring(0, pos) : "";
	    }

	    static BinaryOperatorType? GetBinaryOperatorTypeFromMetadataName(string name)
		{
			switch (name) {
				case "op_Addition":
					return BinaryOperatorType.Add;
				case "op_Subtraction":
					return BinaryOperatorType.Subtract;
				case "op_Multiply":
					return BinaryOperatorType.Multiply;
				case "op_Division":
					return BinaryOperatorType.Divide;
				case "op_Modulus":
					return BinaryOperatorType.Modulus;
				case "op_BitwiseAnd":
					return BinaryOperatorType.BitwiseAnd;
				case "op_BitwiseOr":
					return BinaryOperatorType.BitwiseOr;
				case "op_ExclusiveOr":
					return BinaryOperatorType.ExclusiveOr;
				case "op_LeftShift":
					return BinaryOperatorType.ShiftLeft;
				case "op_RightShift":
					return BinaryOperatorType.ShiftRight;
				case "op_Equality":
					return BinaryOperatorType.Equality;
				case "op_Inequality":
					return BinaryOperatorType.InEquality;
				case "op_LessThan":
					return BinaryOperatorType.LessThan;
				case "op_LessThanOrEqual":
					return BinaryOperatorType.LessThanOrEqual;
				case "op_GreaterThan":
					return BinaryOperatorType.GreaterThan;
				case "op_GreaterThanOrEqual":
					return BinaryOperatorType.GreaterThanOrEqual;
				default:
					return null;
			}
		}
		
		static UnaryOperatorType? GetUnaryOperatorTypeFromMetadataName(string name)
		{
			switch (name) {
				case "op_LogicalNot":
					return UnaryOperatorType.Not;
				case  "op_OnesComplement":
					return UnaryOperatorType.BitNot;
				case "op_UnaryNegation":
					return UnaryOperatorType.Minus;
				case "op_UnaryPlus":
					return UnaryOperatorType.Plus;
				case "op_Increment":
					return UnaryOperatorType.Increment;
				case "op_Decrement":
					return UnaryOperatorType.Decrement;
				default:
					return null;
			}
		}
		
		/// <summary>
		/// This annotation is used to convert a compound assignment "a += 2;" or increment operator "a++;"
		/// back to the original "a = a + 2;". This is sometimes necessary when the checked/unchecked semantics
		/// cannot be guaranteed otherwise (see CheckedUnchecked.ForWithCheckedInitializerAndUncheckedIterator test)
		/// </summary>
		public class RestoreOriginalAssignOperatorAnnotation
		{
			readonly BinaryOperatorExpression binaryOperatorExpression;
			
			public RestoreOriginalAssignOperatorAnnotation(BinaryOperatorExpression binaryOperatorExpression)
			{
				this.binaryOperatorExpression = binaryOperatorExpression;
			}
			
			public AssignmentExpression Restore(Expression expression)
			{
				expression.RemoveAnnotations<RestoreOriginalAssignOperatorAnnotation>();
				AssignmentExpression assign = expression as AssignmentExpression;
				if (assign == null) {
					UnaryOperatorExpression uoe = (UnaryOperatorExpression)expression;
					assign = new AssignmentExpression(uoe.Expression.Detach(), new PrimitiveExpression(1));
				} else {
					assign.Operator = AssignmentOperatorType.Assign;
				}
				binaryOperatorExpression.Right = assign.Right.Detach();
				assign.Right = binaryOperatorExpression;
				return assign;
			}
		}
		
		public override object VisitAssignmentExpression(AssignmentExpression assignment, object data)
		{
			base.VisitAssignmentExpression(assignment, data);
			// Combine "x = x op y" into "x op= y"
			BinaryOperatorExpression binary = assignment.Right as BinaryOperatorExpression;
			if (binary != null && assignment.Operator == AssignmentOperatorType.Assign) {
				if (CanConvertToCompoundAssignment(assignment.Left) && assignment.Left.IsMatch(binary.Left)) {
					assignment.Operator = GetAssignmentOperatorForBinaryOperator(binary.Operator);
					if (assignment.Operator != AssignmentOperatorType.Assign) {
						// If we found a shorter operator, get rid of the BinaryOperatorExpression:
						assignment.CopyAnnotationsFrom(binary);
						assignment.Right = binary.Right;
						assignment.AddAnnotation(new RestoreOriginalAssignOperatorAnnotation(binary));
					}
				}
			}
			if (context.Settings.IntroduceIncrementAndDecrement && (assignment.Operator == AssignmentOperatorType.Add || assignment.Operator == AssignmentOperatorType.Subtract)) {
				// detect increment/decrement
				if (assignment.Right.IsMatch(new PrimitiveExpression(1))) {
					// only if it's not a custom operator
					if (assignment.Annotation<MethodReference>() == null) {
						UnaryOperatorType type;
						// When the parent is an expression statement, pre- or post-increment doesn't matter;
						// so we can pick post-increment which is more commonly used (for (int i = 0; i < x; i++))
						if (assignment.Parent is ExpressionStatement)
							type = (assignment.Operator == AssignmentOperatorType.Add) ? UnaryOperatorType.PostIncrement : UnaryOperatorType.PostDecrement;
						else
							type = (assignment.Operator == AssignmentOperatorType.Add) ? UnaryOperatorType.Increment : UnaryOperatorType.Decrement;
						assignment.ReplaceWith(new UnaryOperatorExpression(type, assignment.Left.Detach()).CopyAnnotationsFrom(assignment));
					}
				}
			}

		    var iexp = (assignment.Left as MemberReferenceExpression);
		    if (iexp != null && iexp.MemberName.Equals("ExceptObject"))
		    {
                //Trace.TraceWarning(iexp.ToString());
		        assignment.Remove(); 
		    }

			return null;
		}
		
		public static AssignmentOperatorType GetAssignmentOperatorForBinaryOperator(BinaryOperatorType bop)
		{
			switch (bop) {
				case BinaryOperatorType.Add:
					return AssignmentOperatorType.Add;
				case BinaryOperatorType.Subtract:
					return AssignmentOperatorType.Subtract;
				case BinaryOperatorType.Multiply:
					return AssignmentOperatorType.Multiply;
				case BinaryOperatorType.Divide:
					return AssignmentOperatorType.Divide;
				case BinaryOperatorType.Modulus:
					return AssignmentOperatorType.Modulus;
				case BinaryOperatorType.ShiftLeft:
					return AssignmentOperatorType.ShiftLeft;
				case BinaryOperatorType.ShiftRight:
					return AssignmentOperatorType.ShiftRight;
				case BinaryOperatorType.BitwiseAnd:
					return AssignmentOperatorType.BitwiseAnd;
				case BinaryOperatorType.BitwiseOr:
					return AssignmentOperatorType.BitwiseOr;
				case BinaryOperatorType.ExclusiveOr:
					return AssignmentOperatorType.ExclusiveOr;
				default:
					return AssignmentOperatorType.Assign;
			}
		}
		
		static bool CanConvertToCompoundAssignment(Expression left)
		{
			MemberReferenceExpression mre = left as MemberReferenceExpression;
			if (mre != null)
				return IsWithoutSideEffects(mre.Target);
			IndexerExpression ie = left as IndexerExpression;
			if (ie != null)
				return IsWithoutSideEffects(ie.Target) && ie.Arguments.All(IsWithoutSideEffects);
			UnaryOperatorExpression uoe = left as UnaryOperatorExpression;
			if (uoe != null && uoe.Operator == UnaryOperatorType.Dereference)
				return IsWithoutSideEffects(uoe.Expression);
			return IsWithoutSideEffects(left);
		}
		
		static bool IsWithoutSideEffects(Expression left)
		{
			return left is ThisReferenceExpression || left is IdentifierExpression || left is TypeReferenceExpression || left is BaseReferenceExpression;
		}
		
		static readonly Expression getMethodOrConstructorFromHandlePattern =
			new TypePattern(typeof(MethodBase)).ToType().Invoke(
				"GetMethodFromHandle",
				new NamedNode("ldtokenNode", new LdTokenPattern("method")).ToExpression().Member("MethodHandle"),
				new OptionalNode(new TypeOfExpression(new AnyNode("declaringType")).Member("TypeHandle"))
			).CastTo(new Choice {
		         	new TypePattern(typeof(MethodInfo)),
		         	new TypePattern(typeof(ConstructorInfo))
		         });
		
		public override object VisitCastExpression(CastExpression castExpression, object data)
		{
			base.VisitCastExpression(castExpression, data);
			// Handle methodof
			Match m = getMethodOrConstructorFromHandlePattern.Match(castExpression);
			if (m.Success) {
				MethodReference method = m.Get<AstNode>("method").Single().Annotation<MethodReference>();
				if (m.Has("declaringType")) {
					Expression newNode = m.Get<AstType>("declaringType").Single().Detach().Member(method.Name);
					newNode = newNode.Invoke(method.Parameters.Select(p => new TypeReferenceExpression(AstBuilder.ConvertType(p.ParameterType, p))));
					newNode.AddAnnotation(method);
					m.Get<AstNode>("method").Single().ReplaceWith(newNode);
				}
				castExpression.ReplaceWith(m.Get<AstNode>("ldtokenNode").Single());
			}
			return null;
		}
		
		void IAstTransform.Run(AstNode node)
		{
			node.AcceptVisitor(this, null);
		}
	}
}
