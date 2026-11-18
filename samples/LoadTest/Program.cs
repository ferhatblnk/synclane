using LoadTest;

// One image, three roles: the pretend API, a SyncLane instance, and the report. See README.md.
var settings = new Settings();

return args.FirstOrDefault() switch
{
    "api" => await PretendApi.RunAsync(settings),
    "worker" => await Worker.RunAsync(settings),
    "report" => await Report.RunAsync(settings),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("Usage: LoadTest api|worker|report. Start everything with samples/LoadTest/run.sh.");
    return 2;
}
