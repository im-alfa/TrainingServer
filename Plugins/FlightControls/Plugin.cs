using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

using TrainingServer;
using TrainingServer.Extensibility;

namespace FlightControls;

public class Plugin : IPlugin
{
#if DEBUG
	public string FriendlyName => "Flight Controls (DEBUG)";
#else
	public string FriendlyName => "Flight Controls";
#endif
    public string Maintainer => "605126 and 519820";

	private readonly Regex _headingRegex, _altitudeRegex, _speedRegex, _squawkRegex, _turnLeftRegex, _turnRightRegex, _directRegex, _holdingRegex, _ilsRegex;

    private static Dictionary<string, bool> aircraftsHolding = new();

    private static Dictionary<string, bool> aircarftOnILS = new();

    private static double Controller(double pos, Runway runway)
    {
        var error = pos - runway.runway_course;

        runway.integralError += error * DistStep;
        var derivativeError = (error - runway.errorLast) / DistStep;
        var output = Kp * error + Ki * runway.integralError + Kd * derivativeError;
        runway.errorLast = error;

        if ((runway.turn_toggle) || (Turn(output + runway.aircraft.TrueCourse, runway.initHdg, runway.turnDir)))
        {
            runway.turn_toggle = true;
            return output + runway.aircraft.TrueCourse;
        }
        // Fix integral wind up
        runway.integralError = 10;
        return runway.aircraft.TrueCourse;
    }

    private class Runway
    {
        public IAircraft aircraft;
        public Coordinate RunwayThreshold;
        public float runway_course;
        public float initHdg;
        public char turnDir;
        public double integralError;
        public double errorLast;
        public bool turn_toggle;
        public float aptalt;
        public float slope;
    }

    private static double Distancenm(Coordinate point1, Coordinate point2)
    {
        const double R = 3443.92; // Earth radius in nm

        var phi1 = point1.Latitude * Math.PI / 180;
        var phi2 = point2.Latitude * Math.PI / 180;

        var Delta_phi = (point2.Latitude - point1.Latitude) * Math.PI / 180;
        var Delta_lambda = (point2.Longitude - point1.Longitude) * Math.PI / 180;

        var a = Math.Sin(Delta_phi / 2) * Math.Sin(Delta_phi / 2) + Math.Cos(phi1) * Math.Cos(phi2) *
            Math.Sin(Delta_lambda / 2) * Math.Sin(Delta_lambda / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return R * c;
    }

    private static double BrngFromVec(double lat1, double lon1, double lat2, double lon2)
    {
        var lat1Rad = lat1 * Math.PI / 180;
        var lat2Rad = lat2 * Math.PI / 180;
        var delta = (lon2 - lon1) * Math.PI / 180;

        var y = Math.Sin(delta) * Math.Cos(lat2Rad);
        var x = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) - Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(delta);

        var brng = Math.Atan2(y, x) * 180 / Math.PI;

        return brng < 0 ? brng + 360 : brng;
    }

    private static bool Turn(double hdg, double initHdg, char turnDir)
    {
        if ((hdg > initHdg) && (turnDir == 'L'))
        {
            return false;
        }

        if ((hdg < initHdg) && (turnDir == 'R'))
        {
            return false;
        }

        return true;
    }

    private static async Task<bool> IlsPid(Runway runway)
    {
        double old_distance = 100;
        var gs_trigger = false;

        var spds = new Dictionary<uint, float>
        {
            {180, 12},
            {160, 9},
            {130, 4}
        };


        while (aircarftOnILS[runway.aircraft.Callsign])
        {
            // Loc
            var rwyOffset = BrngFromVec(runway.aircraft.Position.Latitude, runway.aircraft.Position.Longitude, runway.RunwayThreshold.Latitude, runway.RunwayThreshold.Longitude);
            var acftHdg = Controller(rwyOffset, runway);
            runway.aircraft.TurnCourse((float)acftHdg, 1000F);

            // Spd
            var current_distance = Distancenm(runway.aircraft.Position, runway.RunwayThreshold);
            foreach (var entry in spds.Where(entry => (entry.Value > current_distance) && (entry.Value < old_distance) && (runway.aircraft.GroundSpeed > entry.Key)))
            {
                runway.aircraft.RestrictSpeed(entry.Key, entry.Key, (float)2.5);
            }

            // GS

            // Check if we are near the loc
            if ((rwyOffset - runway.runway_course < 1) && !gs_trigger && (runway.aircraft.Altitude - 100) < (Math.Abs(runway.aptalt + Distancenm(runway.aircraft.Position,
                    runway.RunwayThreshold) * 6076 * Math.Tan(runway.slope))) && (Math.Abs(runway.aptalt + Distancenm(runway.aircraft.Position,
                    runway.RunwayThreshold) * 6076 * Math.Tan(runway.slope))) < (100 + runway.aircraft.Altitude))
            {
                gs_trigger = true;
            }

            // Actualy descend if on gs
            if (gs_trigger)
            {
                runway.aircraft.RestrictAltitude((int)runway.aptalt, (int)runway.aptalt, (uint)((uint)runway.aircraft.GroundSpeed * 101.3
                    * Math.Tan(runway.slope)));
            }

            // Kill aircraft if landed
            if ((runway.aircraft.Altitude < runway.aptalt + 100) && (Distancenm(runway.aircraft.Position, runway.RunwayThreshold) < 0.3))
            {
                runway.aircraft.Kill();
                aircarftOnILS.Remove(runway.aircraft.Callsign);
            }


            Thread.Sleep(1000);

            if (!aircarftOnILS[runway.aircraft.Callsign])
                break;
        }

        aircarftOnILS.Remove(runway.aircraft.Callsign);

        return true;
    }
    private enum Action
    {
		heading, turnLeft, turnRight, altitude, speed, direct, holding
    }
    private const double DistStep = 0.001;
    private const double Kp = 2.5;
    private const double Ki = 0.1;
    private const double Kd = 0.05;

    private string? StartIls(IAircraft aircraft, string message)
    {
        var match = _ilsRegex.Match(message);

        // Perform initial position checks
        var ILS = new Runway
        {
            aircraft = aircraft
        };
        ILS.RunwayThreshold.Latitude = double.Parse(match.Groups["lat"].Value);
        ILS.RunwayThreshold.Longitude = double.Parse(match.Groups["lon"].Value);
        ILS.runway_course = float.Parse(match.Groups["hdg"].Value);
        ILS.initHdg = aircraft.TrueCourse;
        ILS.turn_toggle = false;
        ILS.aptalt = float.Parse(match.Groups["aptalt"].Value);
        ILS.slope = (float)(float.Parse(match.Groups["slope"].Value) * Math.PI / 180);

        var relative = BrngFromVec(ILS.aircraft.Position.Latitude, ILS.aircraft.Position.Longitude, ILS.RunwayThreshold.Latitude, ILS.RunwayThreshold.Longitude);
        var tempRwyHdg = ILS.runway_course;
        var tempAcftHdg = aircraft.TrueCourse;

        // 0 degree fix
        if (tempAcftHdg + 90 < tempRwyHdg)
        {
            tempAcftHdg += 360;
        }
        else if (tempRwyHdg + 90 < tempAcftHdg)
        {
            tempRwyHdg += 360;
            relative += 360;
        }

        if ((tempAcftHdg > relative) && (relative > tempRwyHdg))
        {
            ILS.turnDir = 'L';
        }

        else if ((tempAcftHdg < relative) && (relative < tempRwyHdg))
        {
            ILS.turnDir = 'R';
        }
        else
        {
            return "Already passed the loc";
        }

        if (aircarftOnILS.ContainsKey(aircraft.Callsign)) return "Controller was already started!";
        
        aircarftOnILS.Add(aircraft.Callsign, true);
        var ILStask = Task.Run(() => IlsPid(ILS));

        return "Controller started";

    }

    private class HoldingPattern
    {
        public IAircraft aircraft;
        public float inboundCourse;
        public float outboundCourse;
        public string inboundFix;
        public Coordinate inboundCoordinates;
        public TurnDirection turnDirection;
    }

    readonly RegexOptions rxo = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    public Plugin()
	{

		var regexes = new[]
		{
            @"^(AFTER\s(FH|T)|(FH|T))\s*(?<hdg>\d+(\.\d+)?)",
            @"^(AFTER\sTL|TL)\s*(?<tl>\d+(\.\d+)?)",
            @"^(AFTER\sTR|TR)\s*(?<tr>\d+(\.\d+)?)",
            @"^(EX\s[ACD]|[ACD])\s*(?<alt>\d+)",
			@"^SPD\s*(?<spd>\d+)",
			@"^SQK?\s*(?<sqk>\d{4})",
            @"^(AFTER\sDCT|DCT)\s*(?<dct>\w+(\.\w+)(\/.\w+)(\.\w+)?)",
            @"^(HOLD\sRIGHT|HOLD\sLEFT)\s*(?<ibdCourse>\d+(\.\d+)?)\s(?<hold>\w+(\.\w+)(\/.\w+)(\.\w+)?)",
            @"ILS\s(?<lat>[+-]?\d+(\.\d+)?)[ \/;](?<lon>[+-]?\d+(\.\d+)?);?\s*(?<hdg>\d+);?\s*(?<aptalt>\d+);?\s*(?<slope>[\d.]+)",
        };

		if (File.Exists("commands.re") && File.ReadAllLines("commands.re").Length >= regexes.Length)
			regexes = File.ReadAllLines("commands.re");
		else
			File.WriteAllLines("commands.re", regexes);

		_headingRegex	= new Regex(regexes[0], rxo);
		_turnLeftRegex	= new Regex(regexes[1], rxo);
        _turnRightRegex = new Regex(regexes[2], rxo);
		_altitudeRegex	= new Regex(regexes[3], rxo);
		_speedRegex		= new Regex(regexes[4], rxo);
		_squawkRegex	= new Regex(regexes[5], rxo);
        _directRegex    = new Regex(regexes[6], rxo);
        _holdingRegex   = new Regex(regexes[7], rxo);
        _ilsRegex       = new Regex(regexes[8], rxo);
	}

	private bool TryBreakUp(string message, out object[] fragments, out ushort? squawk)
	{
		List<Tuple<Action, object>> frags = new();
		squawk = null;

		while (!string.IsNullOrWhiteSpace(message))
		{
			while (message.Any() && char.IsPunctuation(message[0]))
				message = message[1..];
			message = message.Trim();

            Match match;
            switch (message)
            {
				case var _ when _headingRegex.IsMatch(message):
                    match = _headingRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.heading, float.Parse(match.Groups["hdg"].Value)));
                    message = message[match.Length..];
					break;

				case var _ when _turnLeftRegex.IsMatch(message):
                    match = _turnLeftRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.turnLeft, float.Parse(match.Groups["tl"].Value)));
                    message = message[match.Length..];
					break;

				case var _ when _turnRightRegex.IsMatch(message):
                    match = _turnRightRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.turnRight, float.Parse(match.Groups["tr"].Value)));
                    message = message[match.Length..];
					break;

                case var _ when _altitudeRegex.IsMatch(message): 
                    match = _altitudeRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.altitude, int.Parse(match.Groups["alt"].Value) * 100));
                    message = message[match.Length..];
                    break;

                case var _ when _speedRegex.IsMatch(message):
                    match = _speedRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.speed, uint.Parse(match.Groups["spd"].Value)));
                    message = message[match.Length..];
                    break;

                case var _ when _squawkRegex.IsMatch(message):
                    match = _squawkRegex.Match(message);
                    squawk = ushort.Parse(match.Groups["sqk"].Value);
                    message = message[match.Length..];
                    break;

                case var _ when _directRegex.IsMatch(message):
                    match = _directRegex.Match(message);
                    frags.Add(new Tuple<Action, object>(Action.direct, match.Groups["dct"].Value));
                    message = message[match.Length..];
                    break;

                case var _ when _holdingRegex.IsMatch(message):
                    match = _holdingRegex.Match(message);

                    var turnDirection = TurnDirection.Right;
                    if (new Regex(@"HOLD\sLEFT\s", rxo).IsMatch(message))
                        turnDirection = TurnDirection.Left;

                    HoldingPattern holding = new()
                    {
                        turnDirection = turnDirection,
                        inboundCourse = float.Parse(match.Groups["ibdCourse"].Value),
                        inboundFix = match.Groups["hold"].Value
                    };

                    frags.Add(new Tuple<Action, object>(Action.holding, holding));
                    message = message[match.Length..];
                    break;

                default:
                    fragments = frags.ToArray();
                    return false;
            }
        }

		fragments = frags.ToArray();
		return frags.Any() || squawk is not null;
	}

	public bool CheckIntercept(string aircraftCallsign, string sender, string message) => 
        TryBreakUp(message, out _, out _) ||
        _ilsRegex.IsMatch(message);

    public void InterruptHoldingOrIls(IAircraft aircraft)
    {
        if (aircarftOnILS.ContainsKey(aircraft.Callsign))
                aircarftOnILS[aircraft.Callsign] = false;

        if (aircraftsHolding.ContainsKey(aircraft.Callsign))
            aircraftsHolding[aircraft.Callsign] = false;
    }

    private static Task<bool> HoldingLoop(HoldingPattern holding)
    {
        var inboundCourse = holding.inboundCourse;
        var inboundCoordinates = holding.inboundCoordinates;
        var turnDirection = holding.turnDirection;
        var aircraft = holding.aircraft;

        var outboundCourse = inboundCourse + 180;
        const int legTime = 60 * 1000; // 1 minutes in ms

        if (outboundCourse > 360.0)
            outboundCourse -= 360;

        // initial turn to the FIX should be at normal turn rate
        var inboundRate = 3f;

        while (aircraftsHolding[aircraft.Callsign])
        {
            // fly inbound
            aircraft.FlyDirect(inboundCoordinates, inboundRate);

            // set holding turn rate for further iterations
            inboundRate = 1.5f;

            // enforce inbound course, I know this is cheating
            // feel free to add a system to make a proper enter on the holding
            aircraft.TurnCourse(inboundCourse, 360f);

            // add outbound turn to the queue
            aircraft.TurnCourse(outboundCourse, 1.5f, turnDirection);

            // give some time to update the present course
            Thread.Sleep(5000);

            // wait until aircraft is heading outbound
            while ((int)aircraft.TrueCourse != (int)outboundCourse && aircraftsHolding[aircraft.Callsign])
            {
                Thread.Sleep(2000);
            }

            // stop holding
            if (!aircraftsHolding[aircraft.Callsign])
                break;

            Thread.Sleep(legTime);
        }

        // pop aircraft from holding list
        aircraftsHolding.Remove(aircraft.Callsign);

        return Task.FromResult(true);
    }

	public string? MessageReceived(IAircraft aircraft, string sender, string message)
	{
        if (_ilsRegex.IsMatch(message))
            return StartIls(aircraft, message);
        

        _ = TryBreakUp(message, out var fragments, out var squawk);
		List<string> msgBacks = new();

		if (squawk is not null)
		{
			try { aircraft.Squawk = squawk.Value; msgBacks.Add("Squawking"); msgBacks.Add(squawk.Value.ToString()); }
			catch { System.Diagnostics.Debug.WriteLine("Invalid squawk " + squawk); }
		}
		else
			msgBacks.Add("Flying");

		foreach (Tuple<Action, object> frag in fragments)
			switch (frag.Item1)
			{
				case Action.heading:
                    if (!new Regex(@"AFTER\sFH\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHoldingOrIls(aircraft);

                    aircraft.TurnCourse((float)frag.Item2);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;
                
                case Action.turnLeft:
					if (!new Regex(@"AFTER\sTL\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHoldingOrIls(aircraft);

                    aircraft.TurnCourse((float)frag.Item2, 3f, TurnDirection.Left);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;
                
                case Action.turnRight:
                    if (!new Regex(@"AFTER\sTR\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHoldingOrIls(aircraft);

                    aircraft.TurnCourse((float)frag.Item2, 3f, TurnDirection.Right);
					msgBacks.Add($"heading {(float)frag.Item2:000.00}");
					break;

                case Action.altitude:
                    Random rnd = new();
                    int climbVerticalSpeed = rnd.Next(1000, 2000), descentVerticalSpeed = rnd.Next(800, 1800);

					// expedite descent/clb
                    if (new Regex(@"EX\s[CD]\s", rxo).IsMatch(message)) {
                        climbVerticalSpeed = 2000; 
                        descentVerticalSpeed = 2500;
                    }

                    var alt = (int)frag.Item2;
                    aircraft.RestrictAltitude(alt, alt, (uint)(aircraft.Altitude > alt ? descentVerticalSpeed : climbVerticalSpeed));
					msgBacks.Add($"altitude {alt / 100:000}");
					break;

				case Action.speed:
                    var spd = (uint)frag.Item2;
                    aircraft.RestrictSpeed(spd, spd, aircraft.GroundSpeed > spd ? 2.5f : 5f);
					msgBacks.Add($"speed {spd:000}");
					break;

                case Action.direct:
                    if (!new Regex(@"AFTER\sDCT\s", rxo).IsMatch(message))
                        aircraft.Interrupt();

                    InterruptHoldingOrIls(aircraft);

                    var dct = (string)frag.Item2;
                    var elems = dct.Split('/');
                    aircraft.FlyDirect(new Coordinate { Latitude = double.Parse(elems[0]), Longitude = double.Parse(elems[1]) });
                    msgBacks.Add($"direct to {dct}");
                    break;

                case Action.holding:
                    var holdingObj = (HoldingPattern)frag.Item2;
                    holdingObj.aircraft = aircraft;

                    var coords = holdingObj.inboundFix.Split('/');

                    // set up coordinates
                    holdingObj.inboundCoordinates.Latitude = double.Parse(coords[0]);
                    holdingObj.inboundCoordinates.Longitude = double.Parse(coords[1]);

                    // start holding
                    if (aircraftsHolding.ContainsKey(aircraft.Callsign)) return "Aircraft was already holding!";
                    aircraftsHolding.Add(aircraft.Callsign, true);
                    Task.Run(() => HoldingLoop(holdingObj));
                    

                    msgBacks.Add($"holding over {holdingObj.inboundFix}");
                    break;

                default:
					System.Diagnostics.Debug.WriteLine("Unknown fragment " + frag.ToString());
					break;
			}

		return msgBacks[0] + " " + string.Join(", then ", msgBacks.Skip(1));
	}
}