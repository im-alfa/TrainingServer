﻿using System;

namespace TrainingServer
{
	public interface IAircraft
	{
		/// <summary>The network callsign of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		string Callsign { get; }

		/// <summary>The current position of the aircraft on the WGS84 spheroid.</summary>
		Coordinate Position { get; }
		
		/// <summary>The present course in degrees true of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		float TrueCourse { get; }
		
		/// <summary>The groundspeed in knots of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		uint GroundSpeed { get; }

		/// <summary>The altimeter reading in feet above mean sea level at standard pressure of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		int Altitude { get; }

		/// <summary>The current squawk code of <see langword="this"/> <see cref="IAircraft"/>.</summary>
		ushort Squawk { get; set; }

		/// <summary>Turns to face a certain course.</summary>
		/// <param name="trueCourse">The course in degrees true to turn to.</param>
		/// <param name="turnRate">The turn rate in degrees per second.</param>
		void TurnCourse(float trueCourse, float turnRate = 3f);

		/// <summary>Flies to a given <see cref="Coordinate"/>.</summary>
		/// <param name="destination">The <see cref="Coordinate"/> to fly to.</param>
		/// <param name="turnRate">The turn rate in degrees per second.</param>
		void FlyDirect(Coordinate destination, float turnRate = 3f);

		/// <summary>Flies a given distance along the present course.</summary>
		/// <param name="distance">The distance in nautical miles to fly.</param>
		void FlyDistance(float distance);

		/// <summary>Flies for a given duration along the present course.</summary>
		/// <param name="duration">The duration to fly.</param>
		void FlyTime(TimeSpan duration);

		/// <summary>Flies an arc at the current radius from the given <paramref name="arcCenterpoint"/>.</summary>
		/// <param name="arcCenterpoint">The centerpoint/origin of the arc.</param>
		/// <param name="degreesOfArc">The number of degrees of arc (clockwise positive) to fly.</param>
		void FlyArc(Coordinate arcCenterpoint, float degreesOfArc);

		/// <summary>Flies until complying with the most recently issued altitude instruction.</summary>
		void FlyAltitude();

		/// <summary>Climbs or descends as needed to comply with the given altitude restriction.</summary>
		/// <param name="minimum">The minimum altitude in feet MSL to climb to.</param>
		/// <param name="maximum">The maximum altitude in feet MSL to descend to.</param>
		/// <param name="climbRate">The vertical velocity magnitude in positive feet per second.</param>
		void RestrictAltitude(int minimum, int maximum, uint climbRate);

		/// <summary>Accelerates or decelerates as needed to comply with the given speed restriction.</summary>
		/// <param name="minimum">The minimum groundspeed in knots to accelerate to.</param>
		/// <param name="maximum">The maximum groundspeed in knots to decelerate to.</param>
		/// <param name="acceleration">The acceleration/deceleration rate in kts per second.</param>
		void RestrictSpeed(uint minimum, uint maximum, float acceleration);
	}

	public interface IServer
	{
		void SpawnAircraft(string callsign, Coordinate startingPosition, float startingCourse, uint startingSpeed, int startingAltitude);
	}

	public struct Coordinate
	{
		public double Latitude { get; set; }
		public double Longitude { get; set; }
	}
}