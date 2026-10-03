using HwProbe;

// M0 hardware probe (docs/design.md, 开发里程碑). See README.md in this folder for what to run.
var opts = Options.Parse(args);
if (opts is null)
{
    Options.PrintUsage();
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

using var sdl = Sdl.Init(opts);
if (sdl is null) return 1;

return opts.Command switch
{
    "list" => ListCommand.Run(),
    "watch" => WatchCommand.Run(opts, cancel.Token),
    "ffb" => FfbCommand.Run(opts, cancel.Token),
    "steer" => SteerCommand.Run(opts, cancel.Token),
    _ => 2,
};
