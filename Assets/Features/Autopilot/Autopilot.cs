
using EdyCommonTools;
using System;
using VehiclePhysics.UI;
using UnityEngine;
using VehiclePhysics;

namespace Perrinn424.AutopilotSystem
{
    public class Autopilot : VehicleBehaviour
    {
        // Public component parameters

        public float kp = 600000.0f;
        public float ki = 0.0f;
        public float kd = 20000.0f;
        public float maxForceP = 10000.0f;
        public float maxForceD = 25000.0f;
        public int startUpThrottleSpeedRatio = 60;
        public int startUpThrottle = 70;
        public int startUpBrakeSpeedRatio = 80;

        public float offsetValue = 0.0f;
        public BoxCollider startLine;

        public bool debugGizmo = false;

        // Exposed for Telemetry

        public float Error => height; //[m]
        public float P => edyPID.proportional; //[N]
        public float I => edyPID.integral; //[N]
        public float D => edyPID.derivative; //[N]
        public float PID => edyPID.output; //[N]


        public AutopilotProvider autopilotProvider;
        readonly PidController edyPID = new PidController();
        private HeuristicFrameSearcher heuristicFrameSearcher;
        private FrameSearcher deprecatedFrameSearcher;

        int sectionSize;
        float height = 0, previousHeight = 0;
        Vector3 appliedForceV3;

        Vector3 m_lastPosition;
        float m_totalDistance, m_lastTime;

        int showSteer, showBrake, showThrottle;
        bool autopilotON;
        bool lostControl = false;

        VPDeviceInput m_deviceInput;
        float m_ffbForceIntensity;
        float m_ffbDamperCoefficient;


        public override int GetUpdateOrder()
        {
            // Execute after input components (0) to override their input
            return 10;
        }


        public override void OnEnableVehicle()
        {
            m_lastPosition = vehicle.transform.position;
            m_totalDistance = 0;
            m_lastTime = 0;

            if (autopilotProvider == null)
            {
                enabled = false;
                return;
            }


            SteeringScreen.autopilotState = false;
            sectionSize = (int)Math.Sqrt(autopilotProvider.Count); // Breakdown recorded replay into even sections
            
            VPReplayAsset replayAsset = autopilotProvider.replayAsset;
            int lookAroundFramesCount = (int)(5f / replayAsset.timeStep); //seconds to look around
            int lookBehind = (int)(lookAroundFramesCount * 0.05f); //5% behind, just in case
            heuristicFrameSearcher = new HeuristicFrameSearcher(replayAsset.recordedData, 5f, lookBehind, lookAroundFramesCount);
            deprecatedFrameSearcher = new FrameSearcher(replayAsset.recordedData);

            m_deviceInput = vehicle.GetComponentInChildren<VPDeviceInput>();
            if (m_deviceInput != null)
            {
                m_ffbForceIntensity = m_deviceInput.forceIntensity;
                m_ffbDamperCoefficient = m_deviceInput.damperCoefficient;
            }
        }


        public override void UpdateVehicle()
        {
            if (Input.GetKeyDown(KeyCode.Q))
            {
                if (autopilotON)
                {
                    autopilotON = false;
                    SteeringScreen.autopilotState = false;
                    if (m_deviceInput != null)
                    {
                        m_deviceInput.forceIntensity = m_ffbForceIntensity;
                        m_deviceInput.damperCoefficient = m_ffbDamperCoefficient;
                    }
                }
                else
                {
                    autopilotON = true;
                    SteeringScreen.autopilotState = true;
                    if (m_deviceInput != null)
                    {
                        m_deviceInput.forceIntensity = 0.0f;
                        m_deviceInput.damperCoefficient = 0.0f;
                    }
                }
            }
        }


        public override void FixedUpdateVehicle()
        {
            Vector3 position = vehicle.transform.position;
            float currentPosX = position.x;
            float currentPosZ = position.z;

            int closestFrame1, closestFrame2;
            float closestDisFrame1, closestDisFrame2;
            CalculateNearestFrame(out closestFrame1, out closestFrame2, out closestDisFrame1, out closestDisFrame2);
            SteeringScreen.bestTime = FramesToTime(closestFrame1);


            if (!autopilotON)
            {
                return;
            }


            // Reference point offset: Recorded vehicle
            Vector3 offsetFromClosestFrame1 = GetOffsetPosition(offsetValue, autopilotProvider[closestFrame1]);
            Vector3 offsetFromClosestFrame2 = GetOffsetPosition(offsetValue, autopilotProvider[closestFrame2]);
            Vector3 offsetFromCurrentVehiclePos = GetOffsetPosition(offsetValue, vehicle.transform.position, vehicle.transform.rotation);

            // get height
            float valueDiffPosX = offsetFromClosestFrame1.x - offsetFromClosestFrame2.x; //recordedReplay[frame3].position.x - recordedReplay[frame4].position.x;
            float valueDiffPosZ = offsetFromClosestFrame1.z - offsetFromClosestFrame2.z; //recordedReplay[frame3].position.z - recordedReplay[frame4].position.z;
            float distanceBetweenTwoFrames = (float)Math.Sqrt((valueDiffPosX * valueDiffPosX) + (valueDiffPosZ * valueDiffPosZ));
            float semiPerimeter = (closestDisFrame1 + closestDisFrame2 + distanceBetweenTwoFrames) / 2;
            float tryCatchArea = semiPerimeter * (semiPerimeter - closestDisFrame1) * (semiPerimeter - closestDisFrame2) * (semiPerimeter - distanceBetweenTwoFrames);

            tryCatchArea = tryCatchArea < 0 ? 0 : tryCatchArea;

            float area = (float)Math.Sqrt(tryCatchArea);
            float checkHeight = area * 2 / distanceBetweenTwoFrames;

            float nextFrameX = autopilotProvider[closestFrame2].position.x - currentPosX;
            float nextFrameZ = autopilotProvider[closestFrame2].position.z - currentPosZ;
            float nextFrameDistance = (float)Math.Sqrt((nextFrameX * nextFrameX) + (nextFrameZ * nextFrameZ));
            float prograssiveCalculation = (float)Math.Sqrt((nextFrameDistance * nextFrameDistance) - (checkHeight * checkHeight));
            int progressive = (int)((distanceBetweenTwoFrames - prograssiveCalculation) / distanceBetweenTwoFrames * 100);

            float errX = offsetFromClosestFrame1.x - offsetFromCurrentVehiclePos.x; //recordedReplay[frame3].position.x - currentPosX;
            float errXBAL = (offsetFromClosestFrame2.x - offsetFromCurrentVehiclePos.x) - errX;
            float errZ = offsetFromClosestFrame1.z - offsetFromCurrentVehiclePos.z; //recordedReplay[frame3].position.z - currentPosZ;
            float errZBAL = (offsetFromClosestFrame2.z - offsetFromCurrentVehiclePos.z) - errZ;
            float degree = -(float)(Math.PI * autopilotProvider[closestFrame1].rotation.eulerAngles.y / 180);
            float degreeERR = -(float)(Math.PI * autopilotProvider[closestFrame2].rotation.eulerAngles.y / 180) - degree;
            float cosD = (float)Math.Cos(degree + degreeERR * progressive / 100);
            float sinD = (float)Math.Sin(degree + degreeERR * progressive / 100);
            float carPosX = ((errX + errXBAL * progressive / 100) * cosD) + ((errZ + errZBAL * progressive / 100) * sinD);
            height = (carPosX > 0) ? -checkHeight : checkHeight;



            float kpTemp = checkHeight == 0 ? kp : Mathf.Min(kp, maxForceP / checkHeight);
            float kiTemp = ki;
            float kdTemp = Mathf.Min(kd, maxForceD * Time.deltaTime / Mathf.Abs(height - previousHeight));

            //get error force
            edyPID.SetParameters(kpTemp, kiTemp, kdTemp);
            edyPID.input = height;
            edyPID.Compute();

            previousHeight = height;

            //errorLimit [m/s]
            appliedForceV3.x = edyPID.output * cosD * 1.000f;
            appliedForceV3.y = 0;
            appliedForceV3.z = edyPID.output * sinD * 1.000f;

            //get recorded driver input
            (closestFrame1, closestFrame2) = GetAsMinMax(closestFrame1, closestFrame2);

            //Car Control System
            float frameAngle = autopilotProvider[closestFrame1].rotation.eulerAngles.y;
            //float carAngle = rigidBody424.rotation.eulerAngles.y;
            float carAngle = vehicle.transform.rotation.eulerAngles.y;

            if ((frameAngle - carAngle) < -350) { frameAngle += 360; }
            else if ((frameAngle - carAngle) > 350) { frameAngle -= 360; }
            float carAngleErr = frameAngle - carAngle;
            carAngleErr = carAngleErr == 0 ? 0 : (float)Math.Sqrt(carAngleErr * carAngleErr);

            if (carAngleErr > 30 && carAngleErr < 90) { lostControl = true; }
            else if (carAngleErr >= 90)
            {
                autopilotON = false;
                SteeringScreen.autopilotState = false;
            }
            else { lostControl = false; }

            if (autopilotON)
            {
                vehicle.cachedRigidbody.AddForceAtPosition(appliedForceV3, offsetFromCurrentVehiclePos); // transform.position rigidBody424.centerOfMass

                if (debugGizmo)
                {
                    DebugUtility.DrawCrossMark(offsetFromCurrentVehiclePos, vehicle.cachedTransform, GColor.pink);
                    Debug.DrawLine(offsetFromCurrentVehiclePos, offsetFromCurrentVehiclePos + appliedForceV3 / 1000.0f, GColor.orange);
                }

                if (!lostControl)
                {
                    // Steer angle
                    int steerERR = autopilotProvider[closestFrame2].inputData[InputData.Steer] - autopilotProvider[closestFrame1].inputData[InputData.Steer];
                    showSteer = (steerERR * progressive / 100) + autopilotProvider[closestFrame1].inputData[InputData.Steer];
                    vehicle.data.Set(Channel.Input, InputData.Steer, showSteer);

                    // Speed check
                    float segmentLength = (autopilotProvider[closestFrame2].position - autopilotProvider[closestFrame1].position).magnitude;
                    //float SecondsPerFrame = Time.time - m_lastTime;
                    float SecondsPerFrame = (closestFrame2 - closestFrame1) * autopilotProvider.TimeStep;
                    m_lastPosition = vehicle.transform.position;
                    m_totalDistance += segmentLength;

                    // Brake Control
                    int brakeERR = autopilotProvider[closestFrame2].inputData[InputData.Brake] - autopilotProvider[closestFrame1].inputData[InputData.Brake];
                    showBrake = (brakeERR * progressive / 100) + autopilotProvider[closestFrame1].inputData[InputData.Brake];

                    if (CheckStartupSpeed(segmentLength, SecondsPerFrame, startUpBrakeSpeedRatio))   //startup
                    {
                        showBrake = 0;
                    }
                    vehicle.data.Set(Channel.Input, InputData.Brake, showBrake);
                    m_lastTime += SecondsPerFrame;

                    // Throttle
                    int throttleERR = autopilotProvider[closestFrame2].inputData[InputData.Throttle] - autopilotProvider[closestFrame1].inputData[InputData.Throttle];
                    showThrottle = (throttleERR * progressive / 100) + autopilotProvider[closestFrame1].inputData[InputData.Throttle];

                    if (CheckStartupSpeed(segmentLength, SecondsPerFrame, startUpThrottleSpeedRatio))   //startup
                    {
                        vehicle.data.Set(Channel.Input, InputData.Throttle, startUpThrottle * 100);
                    }
                    else
                    {
                        vehicle.data.Set(Channel.Input, InputData.Throttle, showThrottle);
                    }

                    // AutomaticGear
                    vehicle.data.Set(Channel.Input, InputData.AutomaticGear, autopilotProvider[closestFrame1].inputData[InputData.AutomaticGear]);
                }
            }
        }

        private void CalculateNearestFrame(out int closestFrame1, out int closestFrame2, out float closestDisFrame1, out float closestDisFrame2)
        {
            // heuristicFrameSearcher is used when the autopilot is on because it trusts that the previous frame was correclty found.
            // When autopilot is off, we still need to find the closest frame because of the SteeringScreen.bestTime.
            // We use the last framesearcher version, because, altough its buggy and it returns false results, it works well in the majority
            // of the cases and it is fast
            IFrameSearcher selectedFrameSearcher = autopilotON ? heuristicFrameSearcher : (IFrameSearcher)deprecatedFrameSearcher;
            selectedFrameSearcher.Search(vehicle.transform);

            closestFrame1 = selectedFrameSearcher.ClosestFrame1;
            closestFrame2 = selectedFrameSearcher.ClosestFrame2;
            closestDisFrame1 = selectedFrameSearcher.ClosestDisFrame1;
            closestDisFrame2 = selectedFrameSearcher.ClosestDisFrame2;
        }

        private bool CheckStartupSpeed(float segmentLength, float SecondsPerFrame, float ratio)
        {
            float speed = segmentLength / SecondsPerFrame;
            float speedRatio = speed * ratio / 100f;
            return vehicle.speed < speedRatio;
        }

        private float FramesToTime(int frames)
        {
            return frames * autopilotProvider.replayAsset.timeStep;
        }

        Vector3 GetOffsetPosition(float offsetValue, VPReplay.Frame offsetTransform)
        {
            return GetOffsetPosition(offsetValue, offsetTransform.position, offsetTransform.rotation);
        }

        Vector3 GetOffsetPosition(float offsetValue, Vector3 position, Quaternion rotation)
        {
            Vector3 positionOffset;

            float degreeOFFSET = (float)(Math.PI * rotation.eulerAngles.y / 180);
            float errOffsetZ = offsetValue;
            float cosDOffset = (float)Math.Cos(degreeOFFSET);
            float sinDOffset = (float)Math.Sin(degreeOFFSET);
            float carPosXoffset = errOffsetZ * sinDOffset;
            float carPosZoffset = errOffsetZ * cosDOffset;

            positionOffset.x = carPosXoffset + position.x;
            positionOffset.y = position.y;
            positionOffset.z = carPosZoffset + position.z;

            return positionOffset;
        }

        (int, int) GetAsMinMax(int valueA, int valueB)
        {
            if (valueA > valueB)
            {
                int tmp = valueA;
                valueA = valueB;
                valueB = tmp;
            }

            return (valueA, valueB);
        }
    }

}