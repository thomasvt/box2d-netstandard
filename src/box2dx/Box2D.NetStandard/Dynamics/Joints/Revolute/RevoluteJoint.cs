﻿/*
  Box2D.NetStandard Copyright © 2020 Ben Ukhanov & Hugh Phoenix-Hulme https://github.com/benzuk/box2d-netstandard
  Box2DX Copyright (c) 2009 Ihar Kalasouski http://code.google.com/p/box2dx
  
// MIT License

// Copyright (c) 2019 Erin Catto

// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
*/


// Point-to-point constraint
// C = p2 - p1
// Cdot = v2 - v1
//      = v2 + cross(w2, r2) - v1 - cross(w1, r1)
// J = [-I -r1_skew I r2_skew ]
// Identity used:
// w k % (rx i + ry j) = w * (-ry i + rx j)

// Motor constraint
// Cdot = w2 - w1
// J = [0 0 -1 0 0 1]
// K = invI1 + invI2

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Box2D.NetStandard.Common;
using Box2D.NetStandard.Dynamics.Bodies;
using Box2D.NetStandard.Dynamics.World;
using Math = Box2D.NetStandard.Common.Math;

namespace Box2D.NetStandard.Dynamics.Joints.Revolute {
  /// <summary>
  /// A revolute joint constrains to bodies to share a common point while they
  /// are free to rotate about the point. The relative rotation about the shared
  /// point is the joint angle. You can limit the relative rotation with
  /// a joint limit that specifies a lower and upper angle. You can use a motor
  /// to drive the relative rotation about the shared point. A maximum motor torque
  /// is provided so that infinite forces are not generated.
  /// </summary>
  public class RevoluteJoint : Joint {
    internal Vector2 m_localAnchorA;
    internal Vector2 m_localAnchorB;
    private Vector3    m_impulse;
    private float   m_motorImpulse;

    private bool  m_enableMotor;
    private float m_maxMotorTorque;
    private float m_motorSpeed;

    private bool  m_enableLimit;
    internal readonly float m_referenceAngle;
    private float m_lowerAngle;
    private float m_upperAngle;

    private int        m_indexA;
    private int        m_indexB;
    private Vector2    m_rA;
    private Vector2    m_rB;
    private Vector2    m_localCenterA;
    private Vector2    m_localCenterB;
    private float      m_invMassA;
    private float      m_invMassB;
    private float      m_invIA;
    private float      m_invIB;
    private Mat33      m_mass;      //effective mass for p2p constraint.
    private float      m_motorMass; // effective mass for motor/limit angular constraint.
    private LimitState m_limitState;

    public override Vector2 GetAnchorA => m_bodyA.GetWorldPoint(m_localAnchorA);

    public override Vector2 GetAnchorB => m_bodyB.GetWorldPoint(m_localAnchorB);

    public override Vector2 GetReactionForce(float invDt) => invDt * new Vector2(m_impulse.X, m_impulse.Y);

    public override float GetReactionTorque(float inv_dt) => inv_dt * m_impulse.Z;

    /// <summary>
    /// Get the current joint angle in radians.
    /// </summary>
    public float JointAngle {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get {
        Body b1 = m_bodyA;
        Body b2 = m_bodyB;
        return b2.m_sweep.a - b1.m_sweep.a - m_referenceAngle;
      }
    }


    /// <summary>
    /// Get the current joint angle speed in radians per second.
    /// </summary>
    public float JointSpeed {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get {
        Body b1 = m_bodyA;
        Body b2 = m_bodyB;
        return b2.m_angularVelocity - b1.m_angularVelocity;
      }
    }

    /// <summary>
    /// Is the joint limit enabled?
    /// </summary>
    public bool IsLimitEnabled {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_enableLimit;
    }

    /// <summary>
    /// Enable/disable the joint limit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnableLimit(bool flag) {
      m_bodyA.SetAwake(true);
      m_bodyB.SetAwake(true);
      m_enableLimit = flag;
    }

    /// <summary>
    /// Get the lower joint limit in radians.
    /// </summary>
    public float LowerLimit {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_lowerAngle;
    }

    /// <summary>
    /// Get the upper joint limit in radians.
    /// </summary>
    public float UpperLimit {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_upperAngle;
    }

    /// <summary>
    /// Set the joint limits in radians.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetLimits(float lower, float upper) {
      //Debug.Assert(lower <= upper);
      m_bodyA.SetAwake(true);
      m_bodyB.SetAwake(true);
      m_lowerAngle = lower;
      m_upperAngle = upper;
    }

    /// <summary>
    /// Is the joint motor enabled?
    /// </summary>
    public bool IsMotorEnabled {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_enableMotor;
    }

    /// <summary>
    /// Enable/disable the joint motor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnableMotor(bool flag) {
      m_bodyA.SetAwake(true);
      m_bodyB.SetAwake(true);
      m_enableMotor = flag;
    }

    /// <summary>
    /// Get\Set the motor speed in radians per second.
    /// </summary>
    public float MotorSpeed {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_motorSpeed;
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      set {
        m_bodyA.SetAwake(true);
        m_bodyB.SetAwake(true);
        m_motorSpeed = value;
      }
    }

    /// <summary>
    /// Set the maximum motor torque, usually in N-m.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMaxMotorTorque(float torque) {
      m_bodyA.SetAwake(true);
      m_bodyB.SetAwake(true);
      m_maxMotorTorque = torque;
    }

    /// <summary>
    /// Get the current motor torque, usually in N-m.
    /// </summary>
    public float MotorTorque {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => m_motorImpulse;
    }

    public RevoluteJoint(RevoluteJointDef def)
      : base(def) {
      m_localAnchorA   = def.localAnchorA;
      m_localAnchorB   = def.localAnchorB;
      m_referenceAngle = def.referenceAngle;

      m_impulse      = new Vector3();
      m_motorImpulse = 0.0f;

      m_lowerAngle     = def.lowerAngle;
      m_upperAngle     = def.upperAngle;
      m_maxMotorTorque = def.maxMotorTorque;
      m_motorSpeed     = def.motorSpeed;
      m_enableLimit    = def.enableLimit;
      m_enableMotor    = def.enableMotor;
      m_limitState      = LimitState.InactiveLimit;
    }

    internal override void InitVelocityConstraints(in SolverData data) {
      m_indexA      = m_bodyA.m_islandIndex;
      m_indexB      = m_bodyB.m_islandIndex;
      m_localCenterA = m_bodyA.m_sweep.localCenter;
      m_localCenterB = m_bodyB.m_sweep.localCenter;
      m_invMassA     = m_bodyA.m_invMass;
      m_invMassB     = m_bodyB.m_invMass;
      m_invIA        = m_bodyA.m_invI;
      m_invIB        = m_bodyB.m_invI;

      float  aA = data.positions[m_indexA].a;
      Vector2 vA = data.velocities[m_indexA].v;
      float  wA = data.velocities[m_indexA].w;

      float  aB = data.positions[m_indexB].a;
      Vector2 vB = data.velocities[m_indexB].v;
      float  wB = data.velocities[m_indexB].w;

      Rot qA = new Rot(aA), qB = new Rot(aB);

      m_rA = Math.Mul(qA, m_localAnchorA - m_localCenterA);
      m_rB = Math.Mul(qB, m_localAnchorB - m_localCenterB);

      // J = [-I -r1_skew I r2_skew]
      //     [ 0       -1 0       1]
      // r_skew = [-ry; rx]

      // Matlab
      // K = [ mA+r1y^2*iA+mB+r2y^2*iB,  -r1y*iA*r1x-r2y*iB*r2x,          -r1y*iA-r2y*iB]
      //     [  -r1y*iA*r1x-r2y*iB*r2x, mA+r1x^2*iA+mB+r2x^2*iB,           r1x*iA+r2x*iB]
      //     [          -r1y*iA-r2y*iB,           r1x*iA+r2x*iB,                   iA+iB]

      float mA = m_invMassA, mB = m_invMassB;
      float iA = m_invIA,    iB = m_invIB;

      bool fixedRotation = (iA + iB == 0.0f);

      m_mass.ex.X = mA + mB + m_rA.Y * m_rA.Y * iA + m_rB.Y * m_rB.Y * iB;
      m_mass.ey.X = -m_rA.Y * m_rA.X                                 * iA - m_rB.Y * m_rB.X * iB;
      m_mass.ez.X = -m_rA.Y                                          * iA - m_rB.Y          * iB;
      m_mass.ex.Y = m_mass.ey.X;
      m_mass.ey.Y = mA + mB + m_rA.X * m_rA.X * iA + m_rB.X * m_rB.X * iB;
      m_mass.ez.Y = m_rA.X                                           * iA + m_rB.X * iB;
      m_mass.ex.Z = m_mass.ez.X;
      m_mass.ey.Z = m_mass.ez.Y;
      m_mass.ez.Z = iA + iB;

      m_motorMass = iA + iB;
      if (m_motorMass > 0.0f) {
        m_motorMass = 1.0f / m_motorMass;
      }

      if (m_enableMotor == false || fixedRotation) {
        m_motorImpulse = 0.0f;
      }

      if (m_enableLimit && fixedRotation == false) {
        float jointAngle = aB - aA - m_referenceAngle;
        if (MathF.Abs(m_upperAngle - m_lowerAngle) < 2.0f * Settings.AngularSlop) {
          m_limitState = LimitState.EqualLimits;
        }
        else if (jointAngle <= m_lowerAngle) {
          if (m_limitState != LimitState.AtLowerLimit) {
            m_impulse.Z = 0.0f;
          }

          m_limitState = LimitState.AtLowerLimit;
        }
        else if (jointAngle >= m_upperAngle) {
          if (m_limitState != LimitState.AtUpperLimit) {
            m_impulse.Z = 0.0f;
          }

          m_limitState = LimitState.AtUpperLimit;
        }
        else {
          m_limitState = LimitState.InactiveLimit;
          m_impulse.Z = 0.0f;
        }
      }
      else {
        m_limitState = LimitState.InactiveLimit;
      }

      if (data.step.warmStarting) {
        // Scale impulses to support a variable time step.
        m_impulse      *= data.step.dtRatio;
        m_motorImpulse *= data.step.dtRatio;

        Vector2 P = new Vector2(m_impulse.X, m_impulse.Y);

        vA -= mA * P;
        wA -= iA * (Vectex.Cross(m_rA, P) + m_motorImpulse + m_impulse.Z);

        vB += mB * P;
        wB += iB * (Vectex.Cross(m_rB, P) + m_motorImpulse + m_impulse.Z);
      }
      else {
        m_impulse = Vector3.Zero;
        m_motorImpulse = 0.0f;
      }

      data.velocities[m_indexA].v = vA;
      data.velocities[m_indexA].w = wA;
      data.velocities[m_indexB].v = vB;
      data.velocities[m_indexB].w = wB;
    }

    internal override void SolveVelocityConstraints(in SolverData data) {
      Vector2 vA = data.velocities[m_indexA].v;
      float  wA = data.velocities[m_indexA].w;
      Vector2 vB = data.velocities[m_indexB].v;
      float  wB = data.velocities[m_indexB].w;

      float mA = m_invMassA, mB = m_invMassB;
      float iA = m_invIA,    iB = m_invIB;

      bool fixedRotation = (iA + iB == 0.0f);

      // Solve motor constraint.
      if (m_enableMotor && m_limitState != LimitState.EqualLimits && fixedRotation == false) {
        float Cdot       = wB - wA - m_motorSpeed;
        float impulse    = -m_motorMass * Cdot;
        float oldImpulse = m_motorImpulse;
        float maxImpulse = data.step.dt * m_maxMotorTorque;
        m_motorImpulse = System.Math.Clamp(m_motorImpulse + impulse, -maxImpulse, maxImpulse);
        impulse        = m_motorImpulse - oldImpulse;

        wA -= iA * impulse;
        wB += iB * impulse;
      }

      // Solve limit constraint.
      if (m_enableLimit && m_limitState != LimitState.InactiveLimit && fixedRotation == false) {
        Vector2 Cdot1 = vB + Vectex.Cross(wB, m_rB) - vA - Vectex.Cross(wA, m_rA);
        float  Cdot2 = wB                               - wA;
        Vector3   Cdot  = new Vector3(Cdot1.X, Cdot1.Y, Cdot2);

        Vector3 impulse = -m_mass.Solve33(Cdot);

        if (m_limitState == LimitState.EqualLimits) {
          m_impulse += impulse;
        }
        else if (m_limitState == LimitState.AtLowerLimit) {
          float newImpulse = m_impulse.Z + impulse.Z;
          if (newImpulse < 0.0f) {
            Vector2 rhs     = -Cdot1 + m_impulse.Z * new Vector2(m_mass.ez.X, m_mass.ez.Y);
            Vector2 reduced = m_mass.Solve22(rhs);
            impulse.X   =  reduced.X;
            impulse.Y   =  reduced.Y;
            impulse.Z   =  -m_impulse.Z;
            m_impulse.X += reduced.X;
            m_impulse.Y += reduced.Y;
            m_impulse.Z =  0.0f;
          }
          else {
            m_impulse += impulse;
          }
        }
        else if (m_limitState == LimitState.AtUpperLimit) {
          float newImpulse = m_impulse.Z + impulse.Z;
          if (newImpulse > 0.0f) {
            Vector2 rhs     = -Cdot1 + m_impulse.Z * new Vector2(m_mass.ez.X, m_mass.ez.Y);
            Vector2 reduced = m_mass.Solve22(rhs);
            impulse.X   =  reduced.X;
            impulse.Y   =  reduced.Y;
            impulse.Z   =  -m_impulse.Z;
            m_impulse.X += reduced.X;
            m_impulse.Y += reduced.Y;
            m_impulse.Z =  0.0f;
          }
          else {
            m_impulse += impulse;
          }
        }

        Vector2 P = new Vector2(impulse.X, impulse.Y);

        vA -= mA * P;
        wA -= iA * (Vectex.Cross(m_rA, P) + impulse.Z);

        vB += mB * P;
        wB += iB * (Vectex.Cross(m_rB, P) + impulse.Z);
      }
      else {
        // Solve point-to-point constraint
        Vector2 Cdot    = vB + Vectex.Cross(wB, m_rB) - vA - Vectex.Cross(wA, m_rA);
        Vector2 impulse = m_mass.Solve22(-Cdot);

        m_impulse.X += impulse.X;
        m_impulse.Y += impulse.Y;

        vA -= mA * impulse;
        wA -= iA * Vectex.Cross(m_rA, impulse);

        vB += mB * impulse;
        wB += iB * Vectex.Cross(m_rB, impulse);
      }

      data.velocities[m_indexA].v = vA;
      data.velocities[m_indexA].w = wA;
      data.velocities[m_indexB].v = vB;
      data.velocities[m_indexB].w = wB;
    }

    internal override bool SolvePositionConstraints(in SolverData data) {
      Vector2 cA = data.positions[m_indexA].c;
      float  aA = data.positions[m_indexA].a;
      Vector2 cB = data.positions[m_indexB].c;
      float  aB = data.positions[m_indexB].a;

      Rot qA = new Rot(aA), qB= new Rot(aB);

      float angularError  = 0.0f;
      float positionError = 0.0f;

      bool fixedRotation = (m_invIA + m_invIB == 0.0f);

      // Solve angular limit constraint.
      if (m_enableLimit && m_limitState != LimitState.InactiveLimit && fixedRotation == false) {
        float angle        = aB - aA - m_referenceAngle;
        float limitImpulse = 0.0f;

        if (m_limitState == LimitState.EqualLimits) {
          // Prevent large angular corrections
          float C = System.Math.Clamp(angle - m_lowerAngle, -Settings.MaxAngularCorrection, Settings.MaxAngularCorrection);
          limitImpulse = -m_motorMass * C;
          angularError = MathF.Abs(C);
        }
        else if (m_limitState == LimitState.AtLowerLimit) {
          float C = angle - m_lowerAngle;
          angularError = -C;

          // Prevent large angular corrections and allow some slop.
          C            = System.Math.Clamp(C + Settings.AngularSlop, -Settings.MaxAngularCorrection, 0.0f);
          limitImpulse = -m_motorMass * C;
        }
        else if (m_limitState ==LimitState.AtUpperLimit) {
          float C = angle - m_upperAngle;
          angularError = C;

          // Prevent large angular corrections and allow some slop.
          C            = System.Math.Clamp(C - Settings.AngularSlop, 0.0f, Settings.MaxAngularCorrection);
          limitImpulse = -m_motorMass * C;
        }

        aA -= m_invIA * limitImpulse;
        aB += m_invIB * limitImpulse;
      }

      // Solve point-to-point constraint.
      {
        qA.Set(aA);
        qB.Set(aB);
        Vector2 rA = Math.Mul(qA, m_localAnchorA - m_localCenterA);
        Vector2 rB = Math.Mul(qB, m_localAnchorB - m_localCenterB);

        Vector2 C = cB + rB - cA - rA;
        positionError = C.Length();

        float mA = m_invMassA, mB = m_invMassB;
        float iA = m_invIA,    iB = m_invIB;

        Matrix3x2 K = new Matrix3x2();
        K.M11 = mA + mB + iA * rA.Y * rA.Y + iB * rB.Y * rB.Y;
        K.M21 = -iA * rA.X * rA.Y - iB * rB.X * rB.Y;
        K.M12 = K.M21;
        K.M22 = mA + mB + iA * rA.X * rA.X + iB * rB.X * rB.X;

        Vector2 impulse = -K.Solve(C);

        cA -= mA * impulse;
        aA -= iA * Vectex.Cross(rA, impulse);

        cB += mB * impulse;
        aB += iB * Vectex.Cross(rB, impulse);
      }

      data.positions[m_indexA].c = cA;
      data.positions[m_indexA].a = aA;
      data.positions[m_indexB].c = cB;
      data.positions[m_indexB].a = aB;

      return positionError <= Settings.LinearSlop && angularError <= Settings.AngularSlop;
    }
  }
}