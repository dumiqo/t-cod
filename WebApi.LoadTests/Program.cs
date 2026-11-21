using WebApi.LoadTests;

return args switch
{
    ["seed", var connection, var outDir] => await Seeder.RunAsync(connection, outDir),
    ["login", var baseUrl, var outDir] => await Login.RunAsync(baseUrl, outDir),
    ["report", var connection, var outDir, var mode] => await Report.RunAsync(connection, outDir, mode),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: seed <connection> <outDir> | login <baseUrl> <outDir> | report <connection> <outDir> <mode>");
    return 2;
}
