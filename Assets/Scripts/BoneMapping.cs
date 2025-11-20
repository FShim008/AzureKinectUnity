using K4AdotNet.BodyTracking;

public static class BoneMapping
{
    public static readonly (JointType, JointType)[] Bones = new (JointType, JointType)[]
    {
        // Spine
        (JointType.SpineNavel, JointType.SpineChest),
        (JointType.SpineChest, JointType.Neck),
        (JointType.Neck, JointType.Head),

        // Left Arm
        (JointType.SpineChest, JointType.ClavicleLeft),
        (JointType.ClavicleLeft, JointType.ShoulderLeft),
        (JointType.ShoulderLeft, JointType.ElbowLeft),
        (JointType.ElbowLeft, JointType.WristLeft),
        (JointType.WristLeft, JointType.HandLeft),
        (JointType.HandLeft, JointType.HandTipLeft),
        (JointType.WristLeft, JointType.ThumbLeft),

        // Right Arm
        (JointType.SpineChest, JointType.ClavicleRight),
        (JointType.ClavicleRight, JointType.ShoulderRight),
        (JointType.ShoulderRight, JointType.ElbowRight),
        (JointType.ElbowRight, JointType.WristRight),
        (JointType.WristRight, JointType.HandRight),
        (JointType.HandRight, JointType.HandTipRight),
        (JointType.WristRight, JointType.ThumbRight),

        // Left Leg
        (JointType.SpineNavel, JointType.HipLeft),
        (JointType.HipLeft, JointType.KneeLeft),
        (JointType.KneeLeft, JointType.AnkleLeft),
        (JointType.AnkleLeft, JointType.FootLeft),

        // Right Leg
        (JointType.SpineNavel, JointType.HipRight),
        (JointType.HipRight, JointType.KneeRight),
        (JointType.KneeRight, JointType.AnkleRight),
        (JointType.AnkleRight, JointType.FootRight),

        // Additional Connections (e.g., between hips/pelvis)
        (JointType.HipLeft, JointType.HipRight)
    };
}