using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class RigConfigs
{
    public IObservable<RigSpecificConfigs> Process(IObservable<String> source)
    {
        return source.Select(value =>
        {
            return GreenDome2configs();
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
        currentRigConfigs.ValveOpenTimeLeft = 30;  // in ms. ~10ms min.
        currentRigConfigs.ValveOpenTimeRight = 30;  // in ms. ~10ms min.

        // MeshMapping for Visual Stimuli
        currentRigConfigs.MeshMapFilePath = @"C:\RigConfigs\MeshMap.csv";  // path to MeshMapping Interpolation file

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

    // Visual Stimuli
    public String MeshMapFilePath;  // MeshMapping Interpolation file
}
