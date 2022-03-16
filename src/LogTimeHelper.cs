using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
//添加Mono.Cecil.dll引用并添加以下代码
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System.Reflection;
//NET6 都实现Log
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console; 


namespace Mono.Cecil.FunLogTimeNet
{
    public static class LogTimeHelper
    {

        /// <summary>
        /// 为了在生产环境得到具体每个函数的执行时间,使用Mono.Cecil，直接外挂到程序集。
        /// 只修改了class中的public、private函数。
        /// 使用了Logging.Abstractions
        /// </summary>
        /// <param name="args"></param>
        public static void Start(string[] args)
        {
            //facotry声明action
            var logFactory = LoggerFactory.Create(logBuilder =>
            {
                //logbuilder 负责装载console， 将log放入di
                logBuilder.AddConsole();
            });

            var Logger = logFactory.CreateLogger("LogTimeHelper");

            //2种注入。1.种输出到console上。
            //2.输出到log文件中(这需要它自己有log的能力)、或者Mono.Cecil嵌入一个日志组件。
            //自己写一个静态单例的Log，通过dll引用的方式加入到每个类的文件中。

            //先用console吧

            Logger.LogInformation("注入StopWatch得到函数运行时间--监控程序，开始启动!");
            Logger.LogInformation("请输入dll文件路径!");
            string path = Console.ReadLine();
            Logger.LogInformation("请选择：");
            Logger.LogInformation("1.使用Console输出到屏幕 ");
            Logger.LogInformation("2.使用D1yuan.InjectionLogger.DLog输出日志文件!");
           
            string logstyle = Console.ReadLine();
            try
            {
                FileStream fileStream = new FileStream(path, FileMode.Open);
                if (fileStream != null)
                {
                    //-定位dll
                    AssemblyDefinition assemblyDefinition = AssemblyDefinition.ReadAssembly(fileStream);//Path: dll or exe Path

                    ModuleDefinition moduleDefinition = assemblyDefinition.MainModule;
                    if (logstyle.Equals("2"))
                    {

                        string folder = Path.GetDirectoryName(path);

                        string giloggerpath = Path.Combine(folder, "D1yuan.InjectionLogger.dll");

                        //DLL所在的绝对路径 
                        Assembly assembly = Assembly.LoadFrom(giloggerpath);

                        // 获取程序集元数据 

                        FileVersionInfo fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

                        //插入程序集引用
                        moduleDefinition.AssemblyReferences.Add(new AssemblyNameReference(fileVersionInfo.ProductName, new Version(fileVersionInfo.ProductVersion)));

                        moduleDefinition.ImportReference(typeof(D1yuan.InjectionLogger.DLog));
                        //加入1个D1yuan.InjectionLogger.DLog类型--需要执行的dll自己引用了D1yuan.InjectionLogger

                    }
                    //-定位类型
                    Collection<TypeDefinition> typeDefinition = moduleDefinition.Types;
                    //循环类型注入
                    foreach (TypeDefinition type in typeDefinition)
                    {

                        //类
                        if (type.IsClass)
                        {

                            Logger.LogInformation("正在注入" + type.FullName + "类");

                            //type.Attributes.Insert(type.Namespace.Count(), "GILogger");




                            //循环类中的函数
                            foreach (MethodDefinition method in type.Methods)
                            {
                                Logger.LogInformation("正在注入" + type.FullName + "类" + method.FullName + "函数");

                                //公共函数public \私有函数 IsPrivate、非构造函数
                                if (!method.IsConstructor && (method.IsPublic || method.IsPrivate || method.IsPublic))
                                {
                                    //1.获取函数内容IL
                                    ILProcessor iLProcessor = method.Body.GetILProcessor();

                                    //2.对函数内容 加入 1个Stopwatch类型变量
                                    TypeReference stopWatchType = moduleDefinition.ImportReference(typeof(Stopwatch));//加入1个Stopwatch类型
                                    VariableDefinition variableDefinition = new VariableDefinition(stopWatchType);
                                    method.Body.Variables.Add(variableDefinition);//加入1个Stopwatch类型变量

                                    //3.得到第一步IL--刚进入函数
                                    Instruction firstInstruction = method.Body.Instructions.First();
                                    //4.在函数内容第一步之前--插入新的Stopwatch对象、并启动Start
                                    iLProcessor.InsertBefore(firstInstruction, iLProcessor.Create(OpCodes.Newobj, moduleDefinition.ImportReference(typeof(Stopwatch).GetConstructor(new Type[] { }))));
                                    iLProcessor.InsertBefore(firstInstruction, iLProcessor.Create(OpCodes.Stloc_S, variableDefinition));
                                    iLProcessor.InsertBefore(firstInstruction, iLProcessor.Create(OpCodes.Ldloc_S, variableDefinition));
                                    iLProcessor.InsertBefore(firstInstruction, iLProcessor.Create(OpCodes.Callvirt, moduleDefinition.ImportReference(typeof(Stopwatch).GetMethod("Start"))));

                                    //5.在函数内容最后一步之前--Stopwatch对象、停止Start
                                    Instruction returnInstruction = method.Body.Instructions.Last();
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Ldloc_S, variableDefinition));
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Callvirt, moduleDefinition.ImportReference(typeof(Stopwatch).GetMethod("Stop"))));

                                    //5.在函数内容最后一步之前-创建字符串，得到 函数名执行时间为 get_ElapsedMilliseconds 方法。
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Ldstr, $"{method.FullName} 耗时(毫秒): "));
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Ldloc_S, variableDefinition));
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Callvirt, moduleDefinition.ImportReference(typeof(Stopwatch).GetMethod("get_ElapsedMilliseconds"))));
                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Box, moduleDefinition.ImportReference(typeof(long))));

                                    iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Call, moduleDefinition.ImportReference(typeof(string).GetMethod("Concat", new Type[] { typeof(object), typeof(object) }))));

                                    if (logstyle.Equals("1"))
                                    {
                                        //6.1类必须使用Console   WriteLine 输出。
                                        iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Call, moduleDefinition.ImportReference(typeof(Console).GetMethod("WriteLine", new Type[] { typeof(string) }))));

                                    }
                                    else if (logstyle.Equals("2"))
                                    {
                                        MethodInfo methodInfolog = typeof(D1yuan.InjectionLogger.DLog).GetMethod("EnqueueMessage"
                                            , new Type[] { typeof(string) });

                                        //6.2.类必须使用D1yuan.InjectionLogger.DLog跨静态类/静态方法AOP
                                        iLProcessor.InsertBefore(returnInstruction, iLProcessor.Create(OpCodes.Call,
                                            moduleDefinition.ImportReference(methodInfolog)
                                            ));
                                    }
                                    //else if (logstyle.Equals("3"))
                                    //{
                                    //    //6.3 类必须能够引用这个组件、using Palas.Common了这个命名空间。


                                    //}
                                    else
                                    {

                                    }
                                }
                            }
                        }
                    }
                    //将注入输出执行时间日志的dll文件重名了并保存。
                    FileInfo fileInfo = new FileInfo(path);
                    string fileName = fileInfo.Name;
                    int pointIndex = fileName.LastIndexOf('.');
                    string frontName = fileName.Substring(0, pointIndex);
                    string backName = fileName.Substring(pointIndex, fileName.Length - pointIndex);
                    string writeFilePath = Path.Combine(fileInfo.Directory.FullName, frontName + "_inject" + backName);
                    //保持修改
                    assemblyDefinition.Write(writeFilePath);
                    Logger.LogInformation($"Success! Output path: {writeFilePath}");
                    fileStream.Dispose();
                }
                else
                {
                    Logger.LogInformation("打不开文件" + path);
                }

            }
            catch (Exception ex)
            {
                Logger.LogInformation("错误：" + ex.Message);
            }
            Logger.LogInformation("执行完成，请点击任意键退出!");
            Console.Read();
        }
    }
}
