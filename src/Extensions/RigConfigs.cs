using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Collections;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class RigConfigs
{
    public String MachineName;

    public IObservable<RigSpecificConfigs> Process(IObservable<String> source)
    {
        return source.Select(value =>
        {
            MachineName = System.Environment.MachineName;  // This fetches the current Rig PCs name to fetch calibrations. Ensure these names are correct and unique!

            Console.WriteLine(MachineName);
            switch (MachineName)
            {
                case "GREEN-DOME-2":
                    return GreenDome2configs();
                case "BLUE-OPTO-1":
                    return BlueOpto1configs();
                case "BLUE-OPTO-2":
                    return BlueOpto2configs();
                default:
                    throw new ArgumentException("Invalide System Name for RigConfig!");
            }
        });
    }

    private RigSpecificConfigs GreenDome2configs()
    {
        RigSpecificConfigs currentRigConfigs = new RigSpecificConfigs();

        // // GreenDome2 // //
        // COM ports
        currentRigConfigs.COMPortBehaviourBoard = "COM10";
        currentRigConfigs.COMPortStepperDriver1 = "COM7";  // Motors 1,2,3
        currentRigConfigs.COMPortStepperDriver2 = "COM8";  // Motors 4,5
        currentRigConfigs.COMPortTimestampGenerator = "COM9";
        currentRigConfigs.COMPortLicketySplitLeft = "COM11";
        currentRigConfigs.COMPortLicketySplitRight = "COM12";
        
        // Cameras
        currentRigConfigs.SerialNumberCamera1 = "23201540";  // Face
        currentRigConfigs.SerialNumberCamera2 = "23199185";  // Pupil
        currentRigConfigs.SerialNumberCamera3 = "25366329";  // Body

        // Valve Calibration
        currentRigConfigs.ValveOpenTimeLeft = 55;  // in ms. ~10ms min.
        currentRigConfigs.ValveOpenTimeRight = 28;  // in ms. ~10ms min.

        // MeshMapping and Gamma calibration for Visual Stimuli
        currentRigConfigs.MeshMapFilePath = @"C:\RigConfigs\MeshMap.csv";  // path to MeshMapping Interpolation file
        currentRigConfigs.GammaLUTFilePath = @"C:\RigConfigs\gammalut.bmp";  // path to Gamma Lookup Table file

        // StepperMotor Default Position
        currentRigConfigs.StepperMotorDefaultPosition = new int[] { 5900, 6040, 4610, 1500, 0 };  // 5 Motors

        return currentRigConfigs;
    }

    private RigSpecificConfigs BlueOpto1configs()
    {
        RigSpecificConfigs currentRigConfigs = new RigSpecificConfigs();

        // COM ports
        currentRigConfigs.COMPortBehaviourBoard = "COM6";
        currentRigConfigs.COMPortStepperDriver1 = "COM3";  // Motors 1,2,3
        currentRigConfigs.COMPortStepperDriver2 = "COM4";  // Motors 4,5
        currentRigConfigs.COMPortTimestampGenerator = "COM5";
        currentRigConfigs.COMPortLicketySplitLeft = "COM7";
        currentRigConfigs.COMPortLicketySplitRight = "COM11";
        
        // Cameras
        currentRigConfigs.SerialNumberCamera1 = "23201535";  // Face
        currentRigConfigs.SerialNumberCamera2 = "26120781";  // Pupil
        currentRigConfigs.SerialNumberCamera3 = "23199186";  // Body

        // Valve Calibration
        currentRigConfigs.ValveOpenTimeLeft = 40;  // in ms. ~10ms min.
        currentRigConfigs.ValveOpenTimeRight = 37;  // in ms. ~10ms min.

        // MeshMapping and Gamma calibration for Visual Stimuli
        currentRigConfigs.MeshMapFilePath = @"C:\RigConfigs\MeshMap.csv";  // path to MeshMapping Interpolation file
        currentRigConfigs.GammaLUTFilePath = @"C:\RigConfigs\gammalut.bmp";  // path to Gamma Lookup Table file

        // StepperMotor Default Position
        currentRigConfigs.StepperMotorDefaultPosition = new int[] { 2300, 7150, 8800, 5750, 6350 };  // 5 Motors

        return currentRigConfigs;
    }

    private RigSpecificConfigs BlueOpto2configs()
    {
        RigSpecificConfigs currentRigConfigs = new RigSpecificConfigs();

        // COM ports
        currentRigConfigs.COMPortBehaviourBoard = "COM11";
        currentRigConfigs.COMPortStepperDriver1 = "COM14";  // Motors 1,2,3
        currentRigConfigs.COMPortStepperDriver2 = "COM12";  // Motors 4,5
        currentRigConfigs.COMPortTimestampGenerator = "COM16";
        currentRigConfigs.COMPortLicketySplitLeft = "COM15";
        currentRigConfigs.COMPortLicketySplitRight = "COM13";
        
        // Cameras
        currentRigConfigs.SerialNumberCamera1 = "25366326";  // Face
        currentRigConfigs.SerialNumberCamera2 = "25366328";  // Pupil
        currentRigConfigs.SerialNumberCamera3 = "25366318";  // Body

        // Valve Calibration
        currentRigConfigs.ValveOpenTimeLeft = 105;  // in ms. ~10ms min.
        currentRigConfigs.ValveOpenTimeRight = 91;  // in ms. ~10ms min.

        // MeshMapping and Gamma calibration for Visual Stimuli
        currentRigConfigs.MeshMapFilePath = @"C:\RigConfigs\MeshMap.csv";  // path to MeshMapping Interpolation file
        currentRigConfigs.GammaLUTFilePath = @"C:\RigConfigs\gammalut.bmp";  // path to Gamma Lookup Table file

        // StepperMotor Default Position
        currentRigConfigs.StepperMotorDefaultPosition = new int[] { 3950, 5800, 7900, 2600, 1700 };  // 5 Motors

        return currentRigConfigs;
    }
}


public class RigSpecificConfigs
{
    // GreenDome2
    // COM ports
    public String COMPortBehaviourBoard;
    public String COMPortStepperDriver1;  // Motors 1,2,3
    public String COMPortStepperDriver2;  // Motors 4,5
    public String COMPortTimestampGenerator;
    public String COMPortLicketySplitLeft;
    public String COMPortLicketySplitRight;
    
    // Cameras
    public String SerialNumberCamera1;  // Face
    public String SerialNumberCamera2;  // Pupil
    public String SerialNumberCamera3;  // Body

    // Valve Calibration
    public Double ValveOpenTimeLeft;  // in ms. ~10ms min.
    public Double ValveOpenTimeRight;  // in ms. ~10ms min.

    // Visual stimulus calibrations
    public String MeshMapFilePath;  // MeshMapping Interpolation file
    public String GammaLUTFilePath;  // Gamma Lookup Table file

    // StepperMotor Default Position
    public int[] StepperMotorDefaultPosition;  // 5 Motors
}
