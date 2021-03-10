﻿using System;
using System.Collections.Generic;
using UnityEngine;

namespace BetterBurnTime
{
    /// <summary>
    /// This is responsible for tracking how long until we impact terrain, and what the required burn
    /// time would be to reach zero velocity right at ground level.  It deliberately does not provide
    /// a burn countdown, since it uses a simplistic model based on looking at the ground elevation
    /// immediately below the ship's current location, so having a countdown would be lethally
    /// misleading.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ImpactTracker : MonoBehaviour
    {
        /// <summary>
        /// The minimum fall speed, in m/s, for tracking impact. If the ship is falling slower
        /// than this (or rising), don't track impact.
        /// </summary>
        private static readonly double MIN_FALL_SPEED = 2.0;

        private static readonly TimeSpan UPDATE_INTERVAL = TimeSpan.FromMilliseconds(250);

        private static ImpactTracker instance = null;

        // The next time we're due to update our calculations. Used with UPDATE_INTERVAL
        // to prevent spamming excessive calculations.
        private DateTime nextVesselHeightUpdate;

        // Results of calculations
        private double lastVesselHeight;
        private double secondsUntilImpact = double.NaN;
        private string impactVerb;
        private Part lowestPart;
        private double impactSpeed;
        private string impactDescription;

        /// <summary>
        /// Global setting for whether impact tracking is enabled.
        /// </summary>
        private static readonly bool displayEnabled = Configuration.showImpact;

        /// <summary>
        /// If calculated time to impact is greater than this many seconds, don't track.
        /// </summary>
        private static readonly double maxTimeToImpact = Configuration.impactMaxTimeUntil;

        /// <summary>
        /// Here when the add-on loads upon flight start.
        /// </summary>
        public void Start()
        {
            try
            {
                instance = this;
                nextVesselHeightUpdate = DateTime.Now;
                Reset();
            }
            catch (Exception e)
            {
                Logging.Exception(e);
            }
        }

        /// <summary>
        /// Called on each frame.
        /// </summary>
        public void LateUpdate()
        {
            try
            {
                if (BurnInfo.OriginalDisplayEnabled)
                {
                    if (HasInfo) Reset();
                }
                else
                {
                    Recalculate();
                }
            }
            catch (Exception e)
            {
                Logging.Exception(e);
            }
        }

        /// <summary>
        /// Gets the projected surface impact speed. Returns NaN if there is none.
        /// </summary>
        public static double ImpactSpeed
        {
            get
            {
                return (instance == null) ? double.NaN : instance.impactSpeed;
            }
        }

        /// <summary>
        /// Gets the description to show for impact (including time-until).
        /// Null if not available.
        /// </summary>
        public static string Description
        {
            get { return (instance == null) ? null : instance.impactDescription; }
        }

        /// <summary>
        /// Gets the time until impact, in seconds.  NaN if not available.
        /// </summary>
        public static double TimeUntil
        {
            get { return (instance == null) ? double.NaN : instance.secondsUntilImpact;  }
        }

        /// <summary>
        /// Do necessary calculations around impact tracking. Returns true if there's anything to display.
        /// It's okay to call frequently, since it uses an update timer to prevent spamming the CPU.
        /// </summary>
        /// <returns></returns>
        private bool Recalculate()
        {
            bool shouldDisplay = displayEnabled;

            // Don't display for bodies with atmospheres. (Kind of a bummer, but acceleration
            // when someone pops a parachute gets stupidly noisy and makes for a bad experience.
            // Just turn it off until I figure out a better solution. This can wait for
            // a future update.
            shouldDisplay &= (FlightGlobals.currentMainBody != null) && !FlightGlobals.currentMainBody.atmosphere;

            double calculatedTimeToImpact = double.NaN;
            if (shouldDisplay)
            {
                calculatedTimeToImpact = CalculateTimeToImpact(FlightGlobals.ActiveVessel, out impactVerb, out impactSpeed);
                if (calculatedTimeToImpact > maxTimeToImpact) shouldDisplay = false;
            }

            if (shouldDisplay)
            {
                // A possible interesting future enhancement: use vessel.terrainNormal to deal better
                // with landing on sloping terrain. Right now we're making the simplifying assumption
                // that ground is flat, and the distance to fall is simply the ship's current height
                // above the ground. The vector vessel.terrainNormal gives the vessel's orientation
                // relative to the ground (i.e. <0,1,0> means "pointing straight away from the ground").
                // We could use that to compensate for sloping ground, so that if we're traveling
                // sideways and the ground is rising or falling beneath us, compensate for the
                // estimated time of impact.
                int remainingSeconds = AsInteger(calculatedTimeToImpact);
                if (remainingSeconds != AsInteger(secondsUntilImpact))
                {
                    secondsUntilImpact = calculatedTimeToImpact;
                    impactDescription = string.Format("{0} in {1}", impactVerb, TimeFormatter.Default.format(AsInteger(secondsUntilImpact)));
                }
            }
            else
            {
                Reset();
            }
            return shouldDisplay;
        }

        public static int AsInteger(double value)
        {
            return (double.IsNaN(value) || double.IsInfinity(value)) ? -1 : (int)value;
        }

        private double GetVesselHeight(out Part currentLowestPart)
        {
            DateTime now = DateTime.Now;
            if (double.IsNaN(lastVesselHeight) || (now > nextVesselHeightUpdate))
            {
                nextVesselHeightUpdate = now + UPDATE_INTERVAL;
                lastVesselHeight = CalculateVesselHeight(FlightGlobals.ActiveVessel, out lowestPart);
            }
            currentLowestPart = lowestPart;
            return lastVesselHeight;
        }

        private static bool IsTimeWarp
        {
            get
            {
                return (TimeWarp.WarpMode == TimeWarp.Modes.HIGH) && (TimeWarp.CurrentRate > 1.0);
            }
        }

        /// <summary>
        /// This method calculates the height in meters from the ship's "current point" to its lowest
        /// extent. Subtracting this number from vessel.altitude gives the altitude of the bottom
        /// of the ship, in the ship's current orientation.
        /// 
        /// Thank you to SirDiazo's mod Landing Height for code that inspired this section.
        /// Landing Height: https://kerbalstuff.com/mod/458/Landing%20Height
        /// 
        /// It's notable that this code is somewhat simplified from SirDiazo's because it's solving
        /// a simpler problem (i.e. it's saving computation by giving more of an approximation).
        /// 
        /// This is a moderately expensive calculation to perform, so use sparingly (i.e. not on
        /// every frame).
        /// </summary>
        /// <returns></returns>
        private static double CalculateVesselHeight(Vessel vessel, out Part lowestPart)
        {
            lowestPart = null;
            if (IsTimeWarp) return 0.0; // vessel is packed, can't do anything
            if (vessel.IsEvaKerbal()) return 0.0; // we don't do this for kerbals
            List<Part> parts = ChooseParts(vessel);
            double minPartPosition = double.PositiveInfinity;
            for (int partIndex = 0; partIndex < parts.Count; ++partIndex)
            {
                Part part = parts[partIndex];
                if ((part.collider == null) || !part.collider.enabled) continue;
                double partPosition = Vector3.Distance(
                    part.collider.ClosestPointOnBounds(vessel.mainBody.position),
                    vessel.mainBody.position);
                if (partPosition < minPartPosition)
                {
                    minPartPosition = partPosition;
                    lowestPart = part;
                }
            }
            if (lowestPart == null) return 0.0; // no usable parts
            double vesselPosition = Vector3.Distance(
                vessel.transform.position,
                vessel.mainBody.position);
            return vesselPosition - minPartPosition;
        }

        private class PartAltitude
        {
            public Part p;
            public double alt;
        }

        private static List<Part> ChooseParts(Vessel vessel)
        {
            // If the vessel's not too big, use all the parts.
            if (vessel.parts.Count < 50) return vessel.parts;

            // Pick the 30 lowest-altitude parts and use those.
            List<PartAltitude> partAltitudes = new List<PartAltitude>(vessel.parts.Count);
            for (int partIndex = 0; partIndex < vessel.parts.Count; ++partIndex)
            {
                Part part = vessel.parts[partIndex];
                if ((part.collider != null) && part.collider.enabled) partAltitudes.Add(
                    new PartAltitude() { p = part, alt = Vector3.Distance(part.transform.position, vessel.mainBody.position) });
            }
            partAltitudes.Sort((p1, p2) => p1.alt.CompareTo(p2.alt));
            int numParts = (partAltitudes.Count > 30) ? 30 : partAltitudes.Count;
            List<Part> parts = new List<Part>(numParts);
            for (int index = 0; index < numParts; ++index)
            {
                parts.Add(partAltitudes[index].p);
            }
            return parts;
        }

        /// <summary>
        /// Gets the time in seconds until the vessel will impact the surface. Returns
        /// positive infinity if it's not applicable.
        /// </summary>
        /// <param name="vessel"></param>
        /// <returns></returns>
        private double CalculateTimeToImpact(Vessel vessel, out string verb, out double impactSpeed)
        {
            verb = "N/A";
            impactSpeed = double.NaN;

            // If we're already landed, or not falling, there's no upcoming impact.
            if (vessel.LandedOrSplashed) return double.PositiveInfinity;
            double fallSpeed = -vessel.verticalSpeed;
            if (fallSpeed < MIN_FALL_SPEED) return double.PositiveInfinity;

            // Are we in orbit (i.e. impact is "never")?
            Orbit orbit = vessel.orbit;
            double periapsis = orbit.semiMajorAxis * (1.0 - orbit.eccentricity);
            float lowestWarpAltitude = vessel.mainBody.timeWarpAltitudeLimits[1]; // lowest altitude at which warp is possible
            if (periapsis > (vessel.mainBody.Radius + lowestWarpAltitude)) return double.PositiveInfinity;

            // How far do we need to fall to hit ground?
            Part currentLowestPart = null;
            double clearance = vessel.altitude - vessel.pqsAltitude - GetVesselHeight(out currentLowestPart);
            verb = "Impact";

            // If we're over water, use water surface instead of ocean floor.
            bool isWater = vessel.mainBody.ocean && (clearance > vessel.altitude);
            if (isWater)
            {
                clearance = vessel.altitude;
                verb = "Splash";
            }

            if (clearance <= 0) return double.PositiveInfinity;

            // Work out centripetal acceleration (i.e. an upward acceleration component
            // due to the planet curving away from us)
            Vector3 shipPosition = vessel.transform.position - vessel.mainBody.position;
            double lateralSpeed = Vector3.Cross(shipPosition.normalized, vessel.obt_velocity).magnitude;
            double centripetalAcceleration = lateralSpeed * lateralSpeed / shipPosition.magnitude;

            // We now know how far we have to fall.  What's our downward acceleration? (might be negative)
            // Note that we deliberately exclude the ship's own propulsion. Including that causes a confusing
            // UI experience.
            double downwardAcceleration = vessel.graviticAcceleration.magnitude - centripetalAcceleration;
            // TODO: If we eventually allow impact-tracking while in atmosphere, we need to do something more
            // complex to allow for atmospheric forces.

            // If our downward acceleration is negative (i.e. net acceleration is upward), there's
            // a chance we may not be due to hit the ground at all. Check for that.
            if (downwardAcceleration < 0)
            {
                double maxFallDistance = -(fallSpeed * fallSpeed) / (2.0 * downwardAcceleration);
                if (maxFallDistance < (clearance + 1.0)) return double.PositiveInfinity;
            }

            // solve the quadratic equation
            double secondsUntilImpact = (-fallSpeed + Math.Sqrt(fallSpeed * fallSpeed + 2.0 * downwardAcceleration * clearance)) / downwardAcceleration;

            double verticalSpeedAtImpact = fallSpeed + secondsUntilImpact * downwardAcceleration;
            impactSpeed = Math.Sqrt(verticalSpeedAtImpact * verticalSpeedAtImpact + vessel.horizontalSrfSpeed * vessel.horizontalSrfSpeed);
            if (!isWater && (lowestPart != null) && (impactSpeed <= lowestPart.crashTolerance))
            {
                verb = "Touchdown";
            }

            return secondsUntilImpact;
        }

        private void Reset()
        {
            lastVesselHeight = double.NaN;
            impactVerb = "N/A";
            lowestPart = null;
            impactSpeed = double.NaN;
            secondsUntilImpact = double.NaN;
            impactDescription = null;
        }

        private bool HasInfo
        {
            get { return !double.IsNaN(impactSpeed); }
        }
    }
}
