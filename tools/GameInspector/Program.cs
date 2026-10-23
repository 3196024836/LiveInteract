using Mono.Cecil;

/// <summary>只读查看本机游戏的类型、签名和指定方法 IL，不加载或执行游戏代码。</summary>
internal static class Program
{
    /// <summary>打印筛选类型或指定方法的元数据。</summary>
    /// <param name="args">程序集路径、类型筛选，或 --il 类型名 方法名。</param>
    /// <returns>检查成功返回零，参数无效返回二。</returns>
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            return 2;
        }

        if(args[0]=="--repack" && args.Length>=4)
        {
            var output=Path.GetFullPath(args[2]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var data=new AssetHelperLib.Repacking.StrippedSceneRepacker().Repack(new(){SceneBundlePath=Path.GetFullPath(args[1]),OutBundlePath=output,ContainerPrefix="liveinteract",ObjectNames=args.Skip(3).ToList()});
            var json=System.Text.Json.JsonSerializer.Serialize(data,new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(output+".json",json);Console.WriteLine(json);return (data.NonRepackedAssets?.Count??0)==0?0:3;
        }
        // 以元数据方式打开文件，避免触发 Unity 静态初始化。
        using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
        var allTypes = assembly.MainModule.Types.SelectMany(Flatten).ToArray();
        if(args.Length>=4&&args[1]=="--calls")
        {
            foreach(var type in allTypes)foreach(var method in type.Methods.Where(m=>m.HasBody))
                if(method.Body.Instructions.Any(i=>i.Operand is MethodReference called&&called.DeclaringType.FullName==args[2]&&called.Name==args[3]))Console.WriteLine(method.FullName);
            return 0;
        }
        if(args.Length>=4&&args[1]=="--writes")
        {
            foreach(var type in allTypes)foreach(var method in type.Methods.Where(m=>m.HasBody))
                if(method.Body.Instructions.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference field&&field.DeclaringType.FullName==args[2]&&field.Name==args[3]))Console.WriteLine(method.FullName);
            return 0;
        }
        if (args.Length >= 4 && args[1] == "--il")
        {
            var type = allTypes.FirstOrDefault(t => t.FullName == args[2]);
            foreach (var method in type?.Methods.Where(m => m.Name == args[3]) ?? [])
            {
                Console.WriteLine(method.FullName);
                if (method.HasBody)
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        Console.WriteLine(instruction);
                    }
                }
            }
            return 0;
        }

        // 类型筛选使用精确名称或名称子串。
        var filters = args.Skip(1).ToArray();
        foreach (var type in allTypes.Where(t => filters.Any(f => t.Name == f || f.StartsWith("~") && t.FullName.Contains(f[1..]))))
        {
            Console.WriteLine("\nTYPE " + type.FullName + " : " + type.BaseType);
            foreach (var field in type.Fields)
            {
                Console.WriteLine("  FIELD " + field.Attributes + " " + field.FieldType + " " + field.Name + (field.HasConstant ? " = " + field.Constant : ""));
            }
            foreach (var property in type.Properties)
            {
                Console.WriteLine("  PROPERTY " + property.PropertyType + " " + property.Name);
            }
            foreach (var method in type.Methods.Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_")))
            {
                Console.WriteLine("  METHOD " + method.Attributes + " " + method.FullName);
            }
        }
        return 0;
    }

    /// <summary>遍历嵌套类型。</summary>
    /// <param name="type">当前类型。</param>
    /// <returns>当前类型及所有子类型。</returns>
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten))
        {
            yield return nested;
        }
    }
}
