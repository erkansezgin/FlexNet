﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.CodeDom.Compiler;
using System.Text.RegularExpressions;
using System.IO;
using SenseNet.ContentRepository.Storage.Scripting;

namespace SenseNet.ContentRepository
{

	[ScriptTagName("jScript")]
	internal class JscriptEvaluator : IEvaluator
	{
		public static readonly string TagName = "jScript";
		private static Type _jsEvaluatorType;

		static JscriptEvaluator()
		{
			//CreateJsEvaluatorAndType();
		}
		private static void CreateJsEvaluatorAndType()
		{
			string codeBase = Path.GetDirectoryName(typeof(JscriptEvaluator).Assembly.CodeBase).Remove(0, 6);

			Microsoft.JScript.JScriptCodeProvider jsCodeProvider = new Microsoft.JScript.JScriptCodeProvider();
			CompilerParameters compilerParam = new CompilerParameters();
			compilerParam.ReferencedAssemblies.Add("System.dll");
			compilerParam.ReferencedAssemblies.Add("System.Data.dll");
			compilerParam.ReferencedAssemblies.Add("System.Xml.dll");
			compilerParam.ReferencedAssemblies.Add("System.Web.dll");
			
			compilerParam.CompilerOptions = "/t:library";
			compilerParam.GenerateInMemory = true;

			string JScriptSource = @"import System;
            import System.Web;
            
            package Evaluator
            {
                class JsEvaluator
                {
					public function WhatIsTheAnswerToLifeTheUniverseAndEverything()
					{
						return 42;
					}
                    public function Eval(expr : String) : String
                    {
                        var result = eval(expr, ""unsafe"");
						if (typeof(result) != ""date"")
							return result;

						var d = new Date(result);
						return d.getYear() + ""-"" + (d.getMonth()+1) + ""-"" + d.getDate() + "" "" + 
							d.getHours() + "":"" + d.getMinutes() + "":"" + d.getSeconds();
                    }
                }
            }";

			CompilerResults compilerResult = jsCodeProvider.CompileAssemblyFromSource(compilerParam, JScriptSource);

			if (compilerResult.Errors.Count > 0)
			{
				string errMsg = String.Format("Compiling JScript code failed and threw the exception: {0}", compilerResult.Errors[0].ErrorText);
				throw new ApplicationException(errMsg);
			}
            
			Assembly assembly = compilerResult.CompiledAssembly;
			_jsEvaluatorType = assembly.GetType("Evaluator.JsEvaluator");
		}

		public string Evaluate(string source)
		{
			if (_jsEvaluatorType == null)
				CreateJsEvaluatorAndType();
            var jsEvaluator = Activator.CreateInstance(_jsEvaluatorType);
			var result = _jsEvaluatorType.InvokeMember("Eval", System.Reflection.BindingFlags.InvokeMethod, null, jsEvaluator, new object[] { source }).ToString();
			return result;
		}
	}
}