// HandMapper.cs
// 손가락 관절 랜드마크를 애니메이터 본 회전값으로 변환
using UnityEngine;

public class HandMapper
{
    public static void Apply(Vector3[] joints, Animator animator, bool isLeft)
    {
        var wrist = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        if (wrist != null) wrist.position = joints[0]; 
    }

    public static float[] CalculateCurls(Vector3[] joints)
    {
        if (joints == null || joints.Length < 21) return new float[5];
        float[] curls = new float[5];
        int[] fingerBases = { 1, 5, 9, 13, 17 };
        for (int i = 0; i < 5; i++)
        {
            int b = fingerBases[i];
            Vector3 v0 = (joints[b + 1] - joints[b]).normalized;
            Vector3 v1 = (joints[b + 2] - joints[b + 1]).normalized;
            Vector3 v2 = (joints[b + 3] - joints[b + 2]).normalized;
            float d1 = Vector3.Dot(v0, v1);
            float d2 = Vector3.Dot(v1, v2);
            float curl = (2.0f - (d1 + d2)) * 0.5f; 
            curls[i] = Mathf.Clamp01(curl * 2.5f); 
        }
        return curls;
    }
}
