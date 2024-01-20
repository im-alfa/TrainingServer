using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TrainingServer;
using TrainingServer.Extensibility;

namespace ScenarioManager;

public class Plugin : IServerPlugin, IPlugin
{
#if DEBUG
    public string FriendlyName => "Scenario Manager (DEBUG)";
#else
	public string FriendlyName => "Scenario Manager";
#endif
    public string Maintainer => "605126";

    private readonly Regex _spawnHeader,
        _heading,
        _speed,
        _altitude,
        _from,
        _to,
        _route,
        _acft,
        _fpAlt;

    private bool _creatingScenario;
    private bool _loadingScenario;
    private bool _authentificationEnabled = true;
    private uint _loginTimeout = 50; // minutes

    private class Sessions
    {
        public string Name { get; set; }
        public DateTime LastCommand { get; set; }
    }

    private List<Sessions> _sessions = new();
    private readonly List<string> _generatedAircraftsCommands = new();
    private List<IAircraft> _generatedAircrafts = new();
    private readonly Dictionary<string, string> _aircraftTypesList = new();

    private const string pluginDirectory = "ScenarioManager";
    private const string scenariosDirectory = $"./{pluginDirectory}/scenarios";
    private const string aircraftsPath = $@"./{pluginDirectory}/aircrafts.csv";
    private const string configPath = $@"./{pluginDirectory}/config.ini";
    private const string passwordsPath = $@"./{pluginDirectory}/passwords.csv";

    private static List<string> ListOfPasswords()
    {
        var passwordList = new List<string>();

        using var reader = new StreamReader(@$"./{pluginDirectory}/passwords.csv");
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            // get passwords
            if (line == null) continue;
            line = line.Split(",")[1];

            // dont add column name as password
            if (line != "pass")
                passwordList.Add(line);
        }

        return passwordList;
    }

    private bool IsUserLogged(string sender)
    {
        return _sessions.Any(session => session.Name == sender);
    }

    private bool LoginAsTrainer(string sender, string password)
    {
        // get password list on each login
        var passwords = ListOfPasswords();

        if (!passwords.Contains(password))
            return false;

        var session = new Sessions
        {
            Name = sender,
            LastCommand = DateTime.Now
        };
        _sessions.Add(session);


        return true;
    }

    private void RenewSession(string sender)
    {
        foreach (var session in _sessions.Where(session => session.Name == sender))
        {
            session.LastCommand = DateTime.Now;
        }
    }

    private void CheckDeadSessions()
    {
        // clone _sessions as we are gonna remove elements at runtime
        foreach (var session in from session in new List<Sessions>(_sessions) let ts = DateTime.Now - session.LastCommand where ts.TotalMinutes > _loginTimeout select session)
        {
            _sessions.Remove(session);
        }
    }

    private void LoadAircrafts()
    {
        using var reader = new StreamReader(aircraftsPath);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line == null) continue;
            var values = line.Split(',');

            _aircraftTypesList.Add(values[0], $"1/{values[0]}/{values[1]}-{values[2]}/{values[3]}");
        }
    }

    private static void CheckRequiredDirectoriesAndFiles()
    {
        var directoriesToCheck = new List<string> { pluginDirectory, scenariosDirectory };

        foreach (var directory in directoriesToCheck.Where(directory => !Directory.Exists(directory)))
            Directory.CreateDirectory(directory);

        var filesToCheck = new List<string> { aircraftsPath, configPath, passwordsPath };

        foreach (var file in filesToCheck.Where(file => !File.Exists(file)))
        {
            Console.WriteLine($"[SCENARIO MANAGER] Required file \"{file}\" doesn't exist!");
            Environment.Exit(0);
        }
        
    }

    private void LoadSettings()
    {
        var settingsIni = new IniFile(configPath);
        _authentificationEnabled = bool.Parse(settingsIni.Read("enabled", "authentication"));
        _loginTimeout = uint.Parse(settingsIni.Read("timeout", "authentication"));
    }

    public Plugin()
    {
        CheckRequiredDirectoriesAndFiles();

        LoadAircrafts();
        LoadSettings();

        var regexes = new[]
        {
            @"^(?<callsign>\w+)\s+AT\s*(?<lat>[+-]?\d+(\.\d+)?)[ /;](?<lon>[+-]?\d+(\.\d+)?);?",
            @"^HDG\s*(?<heading>\d+(.\d+)?)",
            @"^SPD\s*(?<speed>\d+)",
            @"^ALT\s*(?<altitude>-?\d+)",
            @"^RTE(?<route>(\s+[+-]?\d+(\.\d+)?/[+-]?\d+(\.\d+)?)+)",
            @"^FROM\s*(?<from>\w+)",
            @"^TO\s*(?<to>\w+)",
            @"^ACFT\s*(?<acft>\w+)",
            @"^FPALT\s*(?<fpalt>-?\d+)"
        };

        if (File.Exists("scenarioManager.re") && File.ReadAllLines("scenarioManager.re").Length >= regexes.Length)
            regexes = File.ReadAllLines("scenarioManager.re").Select(l => l.Trim()).ToArray();
        else
            File.WriteAllLines("scenarioManager.re", regexes);

        const RegexOptions rxo = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
        _spawnHeader = new Regex(regexes[0], rxo);
        _heading = new Regex(regexes[1], rxo);
        _speed = new Regex(regexes[2], rxo);
        _altitude = new Regex(regexes[3], rxo);
        _route = new Regex(regexes[4], rxo);
        _from = new Regex(regexes[5], rxo);
        _to = new Regex(regexes[6], rxo);
        _acft = new Regex(regexes[7], rxo);
        _fpAlt = new Regex(regexes[8], rxo);
    }

    public bool CheckIntercept(string aircraftCallsign, string sender, string message) => 
        message.Trim().Equals("DIE", StringComparison.InvariantCultureIgnoreCase);

    public bool CheckIntercept(string _, string message) =>
        TryParse(message, out var _1, out var _2, out var _3, out var _4, out var _5, out var _6, out var _7,
            out var _8, out var _9, out var _10) ||
        message.Trim().Equals("START SCENARIO", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Contains("END SCENARIO", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Contains("LOAD SCENARIO", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Contains("LOGIN", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Equals("PAUSEALL", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Equals("RESUMEALL", StringComparison.InvariantCultureIgnoreCase) ||
        message.Trim().Equals("DIEALL", StringComparison.InvariantCultureIgnoreCase);

    private bool TryParse(string message, [NotNullWhen(true)] out string? callsign,
        [NotNullWhen(true)] out (double Lat, double Lon)? position, out float? heading, out uint? speed,
        out int? altitude, out string[]? route, out string? from, out string? to, out string? acftType, out string? fpAlt)
    {
        callsign = null;
        position = null;
        heading = null;
        speed = null;
        altitude = null;
        route = null;
        from = null;
        to = null;
        acftType = null;
        fpAlt = null;

        var match = _spawnHeader.Match(message);
        if (!match.Success)
            return false;

        message = message[match.Length..].TrimStart();

        callsign = (match.Groups["callsign"].Value).ToUpper();
        position = (double.Parse(match.Groups["lat"].Value), double.Parse(match.Groups["lon"].Value));

        while (_altitude.IsMatch(message) || _heading.IsMatch(message) || _speed.IsMatch(message) ||
               _route.IsMatch(message) || _to.IsMatch(message) || _from.IsMatch(message) || _acft.IsMatch(message) || _fpAlt.IsMatch(message))
        {
            match = _altitude.Match(message);
            if (match.Success)
            {
                altitude = int.Parse(match.Groups["altitude"].Value) * 100;
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _fpAlt.Match(message);
            if (match.Success)
            {
                fpAlt = $"F{match.Groups["fpalt"].Value}";
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _heading.Match(message);
            if (match.Success)
            {
                heading = float.Parse(match.Groups["heading"].Value);
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _speed.Match(message);
            if (match.Success)
            {
                speed = uint.Parse(match.Groups["speed"].Value);
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _from.Match(message);
            if (match.Success)
            {
                from = match.Groups["from"].Value;
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _to.Match(message);
            if (match.Success)
            {
                to = match.Groups["to"].Value;
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _acft.Match(message);
            if (match.Success)
            {
                acftType = match.Groups["acft"].Value;
                message = message[match.Length..].TrimStart();
                continue;
            }

            match = _route.Match(message);
            if (match.Success)
            {
                route = match.Groups["route"].Value.Split(Array.Empty<char>(),
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                message = message[match.Length..].TrimStart();
                continue;
            }
        }

        return string.IsNullOrWhiteSpace(message);
    }

    private string CreatePlane(IServer server, string message)
    {
        if (!TryParse(message, out var callsign, out var position, out var heading,
                out var speed, out var altitude, out var route, out var from, out var to, out var acftType, out var fpAlt))
            throw new ArgumentException("Message was not a valid command", nameof(message));

        heading ??= 180f;
        speed ??= 100;
        altitude ??= 100;
        from ??= "ZZZZ";
        to ??= "ZZZZ";
        fpAlt ??= "F???";

        if (acftType != null)
        {
            if (_aircraftTypesList.ContainsKey(acftType))
                acftType = _aircraftTypesList[acftType];
        }
        else
        {
            acftType = "1/UNKN/?-?/?";
        }

        if (server.SpawnAircraft(
                callsign,
                new Flightplan('I', 'S', acftType, "N????", from.ToUpper(), new(), new(), fpAlt, to.ToUpper(), 0, 0, 0, 0, "????",
                    "RMK/PLUGIN GENERATED AIRCRAFT. FLIGHT PLAN MAY BE INACCURATE.",
                    string.Join(' ', route ?? new[] { "DCT" })),
                new Coordinate { Latitude = position.Value.Lat, Longitude = position.Value.Lon },
                heading.Value, speed.Value, altitude.Value) is IAircraft ac)
        {
            if (route is not null)
                foreach (var wp in route)
                {
                    var elems = wp.Split('/');
                    ac.FlyDirect(new Coordinate { Latitude = double.Parse(elems[0]), Longitude = double.Parse(elems[1]) });
                }

            _generatedAircrafts.Add(ac);

            if (_creatingScenario)
            {
                _generatedAircraftsCommands.Add(message);
            }

            if (_creatingScenario || _loadingScenario)
            {
                ac.Paused = true;
            }

            return $"Spawned aircraft {callsign}.";
        }

        return $"Aicraft with callsign {callsign} already exists. Spawning failed.";
    }

    // message received for IPlugin
    public string? MessageReceived(IAircraft aircraft, string sender, string message)
    {
        if (!message.Trim().Equals("DIE", StringComparison.InvariantCultureIgnoreCase)) return "";
        try
        {
            // pop from generated list
            _generatedAircrafts.Remove(aircraft);
            aircraft.Kill();
            return "Goodbye!";
        }
        catch
        {
            // fail silently
        }

        return "";
    }

    // message Received for IServerPlugin
    public string? MessageReceived(IServer server, string sender, string message)
    {
        // login routine
        if (message.Trim().Contains("LOGIN", StringComparison.InvariantCultureIgnoreCase))
        {
            var password = Regex.Match(message, @"LOGIN (?<password>\w+)").Groups["password"].Value;

            return LoginAsTrainer(sender, password) ? "Logged in successfully." : "Incorrect password. Try again.";
        }

        if (_authentificationEnabled) {
            // check dead sessions
            CheckDeadSessions();

            // check if logged
            if (!IsUserLogged(sender))
                return "You are not allowed to use this command. Please log in";
            
            RenewSession(sender);
        }

        if (message.Trim().Equals("START SCENARIO", StringComparison.InvariantCultureIgnoreCase))
        {
            if (_creatingScenario)
                return "You're already in creator mode.";

            _creatingScenario = true;
            return "Scenario creator started";
        }

        if (message.Trim().Contains("END SCENARIO", StringComparison.InvariantCultureIgnoreCase))
        {
            if (!_creatingScenario)
                return "You you're not creating an scenario";

            // extract scenario name to save
            var scenarioName = Regex.Match(message, @"END SCENARIO (?<fname>\w+)");

            if (!scenarioName.Success)
                return "Couldnt read scenario name";

            var fname = scenarioName.Groups["fname"].Value;

            // dont hack me please
            if (new[] { ".", "\\", "/" }.Any(fname.Contains))
                return "Invalid filename.";

            // start saving scenario
            using (var writeScenario = new StreamWriter($"{scenariosDirectory}/{fname}.scenery"))
            {
                foreach (var command in _generatedAircraftsCommands)
                    writeScenario.WriteLine(command);
            }

            _creatingScenario = false;
            _generatedAircraftsCommands.Clear();

            return $"Scenario saved as {fname}";
        }

        if (message.Trim().Contains("LOAD SCENARIO", StringComparison.InvariantCultureIgnoreCase))
        {
            // extract scenario name to save
            var scenarioName = Regex.Match(message, @"LOAD SCENARIO (?<fname>\w+)");

            if (!scenarioName.Success)
                return "Couldn't read scenario name";

            var fname = scenarioName.Groups["fname"].Value;

            // check if scenario exists
            var exists = File.Exists($"{scenariosDirectory}/{fname}.scenery");

            if (!exists)
                return "That scenario doesn't exist";
            _loadingScenario = true;
            using var reader = new StreamReader(@$"{scenariosDirectory}/{fname}.scenery");
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();

                if (line != null) CreatePlane(server, line);
            }

            _loadingScenario = false;
            return "Scenario loaded successfully. ";
        }

        if (message.Trim().Equals("PAUSEALL", StringComparison.InvariantCultureIgnoreCase))
        {
            foreach (var aircraft in _generatedAircrafts)
            {
                aircraft.Paused = true;
            }
            
            return "Simulation paused.";
        }

        if (message.Trim().Equals("RESUMEALL", StringComparison.InvariantCultureIgnoreCase))
        {
            foreach (var aircraft in _generatedAircrafts)
            {
                aircraft.Paused = false;
            }

            return "Simulation resumed.";
        }

        if (message.Trim().Equals("DIEALL", StringComparison.InvariantCultureIgnoreCase))
        {
            // create a copy of generatedAircrafts as we are going to remove elements at runtime
            foreach (var aircraft in new List<IAircraft>(_generatedAircrafts))
            {
                try
                {
                    // POP FROM GENERATED ACFTS
                    _generatedAircrafts.Remove(aircraft);
                    aircraft.Kill();
                }
                catch
                {
                    // fail silently
                }
            }

            return "Goodbye!";
        }

        return CreatePlane(server, message);
    }
}