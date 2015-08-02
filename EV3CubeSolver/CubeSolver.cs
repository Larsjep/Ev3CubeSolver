using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KociembaTwoPhase;
using MonoBrickFirmware.Display;
using MonoBrickFirmware.Management;
using MonoBrickFirmware.Movement;
using MonoBrickFirmware.Sensors;
using MonoBrickFirmware.UserInput;
using Color = System.Drawing.Color;

namespace EV3CubeSolver
{
    public class CubeSolver : RobotBase
    {
        private static string LogPath
        {
            get
            {
                string filePath = new Uri(Assembly.GetExecutingAssembly().CodeBase).AbsolutePath;
                return Path.GetDirectoryName(filePath) + "/cube_solver_log.txt";
            }
        }

        private readonly Dictionary<ArmPos, int> armPositions
            = new Dictionary<ArmPos, int> {{ArmPos.PARK, 0}, {ArmPos.READY, 5}, {ArmPos.HOLD, 105}, {ArmPos.FLIP, 190}};

        private readonly Dictionary<SensorPos, int> sensorPositions
            = new Dictionary<SensorPos, int> { { SensorPos.PARK, 0 }, { SensorPos.READY, -400 }, { SensorPos.FIRST, -590 }, { SensorPos.SECOND, -635 }, { SensorPos.THIRD, -725 } };

        private readonly TextWriter logWriter = new StreamWriter(LogPath);

        private readonly EV3ColorSensor colorSensor = new EV3ColorSensor(SensorPort.In2);
        private readonly Motor motorArm = new Motor(MotorPort.OutB);
        private readonly Motor motorBasket = new Motor(MotorPort.OutD);
        private readonly Motor motorSensor = new Motor(MotorPort.OutC);
        private readonly EV3TouchSensor touchSensor = new EV3TouchSensor(SensorPort.In1);

        private readonly int[] scanColorSequence =
        {
            8, 7, 4, 1, 2, 3, 6, 9, 5,
            4, 1, 2, 3, 6, 9, 8, 7, 5,
            8, 7, 4, 1, 2, 3, 6, 9, 5,
            6, 9, 8, 7, 4, 1, 2, 3, 5,
            4, 1, 2, 3, 6, 9, 8, 7, 5,
            2, 3, 6, 9, 8, 7, 4, 1, 5
        };

        private readonly int[] scanFaceSequence = {0, 18, 36, 27, 45, 9};

        private readonly char[] facelets = new char[54];

        private readonly ManualResetEvent waitArm = new ManualResetEvent(true);
        private readonly ManualResetEvent waitBasket = new ManualResetEvent(true);
        private readonly ManualResetEvent waitSensor = new ManualResetEvent(true);

        private readonly ManualResetEvent readDataWaitHandle = new ManualResetEvent(false);

        private State robotState = State.SCAN_COLORS;

        private char down = 'D';
        private char up = 'U';
        private char[] mid = {'R', 'F', 'L', 'B'};

        private string solution;
        private int calibrationState;

        public CubeSolver() : base(0, 5d) {}

        private void MotorsOff()
        {
            motorArm.Off();
            motorSensor.Off();
            motorBasket.Off();

            motorArm.ResetTacho();
            motorBasket.ResetTacho();
            motorSensor.ResetTacho();
        }

        private void TwistBasket(bool reverse, int quarters, int overTwist = 50)
        {
            WaitForBasketReady();
            waitBasket.Reset();

            int move = 135*quarters - 25 + overTwist;
            sbyte speed = reverse ? (sbyte) -75 : (sbyte) 75;
            WaitHandle waitHandle = motorBasket.SpeedProfile(speed, 15, (uint) move, 10, true);

            Task.Factory.StartNew(() =>
            {
                waitHandle.WaitOne();
                if (overTwist != 0)
                {
                    motorBasket.SpeedProfile((sbyte) -speed, 0, (uint) overTwist, 0, true).WaitOne();
                }
                waitBasket.Set();
            });
        }

        private void MoveArm(ArmPos pos)
        {
            WaitForArmReady();
            waitArm.Reset();
            int currTacho = motorArm.GetTachoCount();

            int move = armPositions[pos] - currTacho;
            sbyte speed = move < 0 ? (sbyte) -50 : (sbyte) 50;
            int rampDownVal = Math.Abs(move) > 15 ? 15 : 0;
            WaitHandle waitHandle = motorArm.SpeedProfile(speed, 0, (uint) Math.Abs(move) - (uint) rampDownVal, (uint) rampDownVal, true);
            Task.Factory.StartNew(() =>
            {
                waitHandle.WaitOne();
                waitArm.Set();
            });
        }

        private void ParkSensorArm()
        {
            if (touchSensor.IsPressed()) return;
            motorSensor.SetSpeed(20);
            while (!touchSensor.IsPressed())
            {
                Thread.Sleep(50);
            }
            motorSensor.Brake();
            motorSensor.ResetTacho();
        }

        private void WaitForSensorReady()
        {
            waitSensor.WaitOne(10000);
        }

        private void WaitForArmReady()
        {
            waitBasket.WaitOne(10000);
            waitSensor.WaitOne(10000);
            waitArm.WaitOne(10000);
        }

        private void WaitForBasketReady()
        {
            waitArm.WaitOne(10000);
            waitBasket.WaitOne(10000);
        }

        private void MoveSensor(SensorPos pos)
        {
            WaitForSensorReady();
            if (pos == SensorPos.PARK)
            {
                ParkSensorArm();
            }
            else
            {
                waitSensor.Reset();
                int currTacho = motorSensor.GetTachoCount();
                int move = sensorPositions[pos] - currTacho;
                sbyte speed = move < 0 ? (sbyte) -35 : (sbyte) 35;
                /*WaitHandle waitHandle = */motorSensor.SpeedProfile(speed, 0, (uint) Math.Abs(move - 5), 5, true);
                Task.Factory.StartNew(() =>
                {
                    if (Math.Abs(move) > 200) 
                        Thread.Sleep(Math.Abs(move)*4);
                    else
                        Thread.Sleep(Math.Abs(move)*15);
                    //waitHandle.WaitOne();
                    waitSensor.Set();
                });
            }
        }

        private string ScanColor()
        {
            WaitForSensorReady();

            RGBColor rgbColor = colorSensor.ReadRGB();
            Color color = Color.FromArgb(rgbColor.Red, rgbColor.Green, rgbColor.Blue);
            var hue = (int) color.GetHue();
            var bright = (int) (color.GetBrightness()*100);
            if ((bright < 15 && hue < 50) || (bright <= 22 && hue < 30) || bright < 15)
            {
                LcdConsole.WriteLine("Color scan error");
                return "ERR";
            }
            //int sat = (int) (color.GetSaturation()*100);
            string colorStr = string.Format("R:{0} G:{1} B:{2} H:{3} B:{4} ", rgbColor.Red, rgbColor.Green, rgbColor.Blue, hue, bright);

            string c = " ";
            if ((bright > 56) ||
                (hue > 42 && hue < 90 && (float) rgbColor.Red/rgbColor.Blue < 1.97) ||
                (hue >= 90 && hue < 105 && bright > 40))
            {
                c = "U - WHITE";
            }
            else if (hue > 80 && hue < 135)
            {
                c = "B - GREEN";
            }
            else if (hue > 135 && hue < 260)
            {
                c = "F - BLUE";
                if (((float) rgbColor.Blue)/rgbColor.Red < 1.2f) c = "U - WHITE";
            }
            else if (hue > 33 && hue < 70)
            {
                c = "D - YELLOW";
            }

            else if (hue > 5 && hue < 22)
            {
                if (rgbColor.Blue >= 15 && hue > 15)
                {
                    if (((float) rgbColor.Red)/rgbColor.Blue > 6.10f)
                        c = "R - ORANGE";
                    else
                        c = "L - RED";
                }
                else
                    c = "R - ORANGE";
            }
            else if (hue >= 22)
            {
                c = "L - RED";
            }

            c += " - " + colorStr;
            return c;
        }

        private void ScanColors()
        {
            for (int i = 1; i < 55; i++)
            {
                string c;
                
                if (i == 9) 
                    c = "U - WHITE";
                else
                {
                    if (i != 0 && i%9 == 0) 
                        MoveSensor(SensorPos.THIRD);
                    else
                    {
                        MoveSensor((i - (i/9)*9)%2 != 0 ? SensorPos.SECOND : SensorPos.FIRST);
                    }

                    while (true)
                    {
                        c = ScanColor();
                        if (c == "ERR")
                            CalibrateSensorPosition();
                        else
                            break;
                    }
                    ResetSensorPosition();
                }

                int seq = scanFaceSequence[(i - 1)/9] - 1;
                facelets[scanColorSequence[(i - (9*((i - 1)/9))) + seq] + seq] = c.Length > 0 ? c[0] : 'X';
                
                logWriter.WriteLine(c);
                if (i%9 == 0) logWriter.WriteLine("");
                logWriter.Flush();

                if (i%9 == 0)
                {
                    if (i != 9)
                    {
                        TwistBasket(i%2 == 0, 2, 0);
                    }
                    MoveSensor(SensorPos.READY);
                    MoveArm(ArmPos.FLIP);
                    MoveArm(ArmPos.READY);
                }
                else 
                    TwistBasket(true, 1, 0);
            }
        }

        private void ResetSensorPosition()
        {
            if (calibrationState == 2 || calibrationState == 3)
            {
                motorBasket.SpeedProfile(25, 5, 25, 5, true).WaitOne();
            }
            if(calibrationState > 0) Thread.Sleep(200);
            calibrationState = 0;
        }

        private void CalibrateSensorPosition()
        {
            WaitHandle waitHandle = null;
            switch (calibrationState)
            {
                case 0:
                    waitHandle = motorSensor.SpeedProfile(25, 3, 12, 3, true);
                    Thread.Sleep(16 * 15);
                    calibrationState++;
                    break;
                case 1:
                    waitHandle = motorBasket.SpeedProfile(-25, 5, 25, 5, true);             
                    calibrationState++;
                    break;
                case 2:
                    waitHandle = motorSensor.SpeedProfile(-25, 3, 30, 3, true);
                    Thread.Sleep(600);
                    calibrationState++;
                    break;
                case 3:
                    motorSensor.SpeedProfile(25, 3, 12, 3, true);
                    waitHandle = motorBasket.SpeedProfile(25, 5, 25, 5, true);
                    calibrationState = 0;
                    break;
            }
            if (waitHandle != null) waitHandle.WaitOne();
        }

        private void Solve()
        {
            string[] moves = solution.Split(' ');
            char face = ' ';

            foreach (string move in moves)
            {
                bool qw = true;
                int qty = 1;
                if (move.Length > 0)
                    face = move[0];
                if (move.Length > 1)
                {
                    char movePart = move[1];
                    if (movePart == '\'')
                    {
                        qw = false;
                    }
                    else
                    {
                        int.TryParse(movePart.ToString(CultureInfo.InvariantCulture), out qty);
                    }
                }
                if (move.Length > 2 && move[2] == '\'') qw = false;


                if (down == face)
                {
                    MoveArm(ArmPos.HOLD);
                }
                else if (up == face)
                {
                    MoveArm(ArmPos.FLIP);
                    MoveArm(ArmPos.HOLD);
                    MoveArm(ArmPos.FLIP);
                    MoveArm(ArmPos.HOLD);
                    char front = mid[0];
                    up = down;
                    down = face;
                    mid[0] = mid[2];
                    mid[2] = front;
                }
                else
                {
                    int destPos;
                    for (destPos = 0; destPos < 4; destPos++) if (face == mid[destPos]) break;
                    if (destPos > 0)
                    {
                        if (destPos == 3)
                        {
                            TwistBasket(false, 2, 0);
                        }
                        else
                        {
                            TwistBasket(true, 2*destPos, 0);
                        }
                        mid = mid.Skip(destPos).Concat(mid.Take(destPos)).ToArray();
                    }

                    MoveArm(ArmPos.FLIP);
                    MoveArm(ArmPos.HOLD);
                    char d = down;
                    char u = up;
                    down = mid[0];
                    up = mid[2];
                    mid[2] = d;
                    mid[0] = u;
                }


                TwistBasket(qw, 2*qty);
                MoveArm(ArmPos.READY);
                logWriter.WriteLine("U:{0} D:{1} F:{2} R:{3} B{4} L:{5}", up, down, mid[0], mid[1], mid[2], mid[3]);
            }
        }

        protected override void UpdateRobot()
        {
            try
            {
                switch (robotState)
                {
                    case State.SCAN_COLORS:
                        Buttons.LedPattern(7);
                        ScanColors();
                        logWriter.WriteLine("");
                        logWriter.WriteLine(new string(facelets));
                        robotState = State.CALC;
                        break;
                    case State.CALC:
                        readDataWaitHandle.WaitOne();
                        Buttons.LedPattern(9);
                        //MotorsOff();
                        var f = new string(facelets);
                        //var f = "BFDFULUDRBULFRURULRLDUFLFBFDRDBDRFBURDFDLBULLBRUFBRBDL";
                        LcdConsole.WriteLine(f);
                        solution = Search.solution(f, 24, false).TrimEnd();
                        LcdConsole.WriteLine(solution);
                        logWriter.WriteLine("");
                        logWriter.WriteLine(solution);
                        robotState = State.SOLVE;
                        break;
                    case State.SOLVE:
                        if (!solution.ToUpper().StartsWith("ERR"))
                        {
                            Buttons.LedPattern(5);
                            Solve();
                        }
                        else
                        {
                            Buttons.LedPattern(2);
                        }
                        robotState = State.STOP;
                        break;
                }
            }
            catch (Exception e)
            {
                logWriter.WriteLine(e);
            }
        }

        protected override void StartRobot()
        {
            try
            {
                MotorsOff();
                LcdConsole.WriteLine("Battery: {0}", Battery.Voltage);
                Task.Factory.StartNew(() => { 
                    LcdConsole.WriteLine(CoordCube.PrunPath); //static constructor reads the prun file
                    readDataWaitHandle.Set();
                });
                colorSensor.Mode = ColorMode.RGB;
                MoveSensor(SensorPos.PARK);
            }
            catch (Exception e)
            {
                logWriter.WriteLine(e);
            }
        }

        protected override void StopRobot()
        {
            logWriter.Close();
            Buttons.LedPattern(0);
            MoveArm(ArmPos.PARK);
            MoveSensor(SensorPos.PARK);
            MotorsOff();
        }

        private enum ArmPos
        {
            PARK,
            READY,
            HOLD,
            FLIP
        }

        private enum SensorPos
        {
            PARK,
            READY,
            FIRST,
            SECOND,
            THIRD
        }

        private enum State
        {
            SCAN_COLORS,
            CALC,
            SOLVE,
            STOP
        }
    }
}