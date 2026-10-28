using System;
using System.Collections.Generic;
using System.Linq;
using CodexCompanion;

public class AnimationControllerTests
{
    static int checks;
    static void Check(bool ok, string name)
    {
        if (!ok) { Console.WriteLine("FAILED " + name); Environment.Exit(1); }
        checks++; Console.WriteLine("PASS " + name);
    }
    static AnimationPose T(AnimationController c, double dt, double distance, CompanionWorkState state = CompanionWorkState.Idle)
    {
        return c.Tick(dt, distance, false, false, state);
    }
    public static void Main()
    {
        AnimationController c = new AnimationController(1729);
        Check(c.InteractionNames.Length == 8 && c.InteractionClipSequences.Distinct().Count() == 8, "eight structurally distinct approach sequences");
        Check(c.IdleNames.Length == 8 && c.WorkNames.Length == 6, "eight idle details and six work variants");
        Check(T(c,0,1000).AllowGaze,"base idle permits native sixteen-direction gaze");
        List<string> approaches = new List<string>();
        for (int i=0;i<24;i++)
        {
            if (i>0) T(c,6.1,300);
            AnimationPose p=T(c,0,100);
            approaches.Add(p.DisplayName);
            Check(p.Family=="interaction" && !p.AllowGaze && p.IsSpecial,"approach action " + (i+1) + " begins");
        }
        Check(Enumerable.Range(0,3).All(delegate(int bag) { return approaches.Skip(bag*8).Take(8).Distinct().Count()==8; }),"every shuffle round contains eight unique actions");
        Check(!Enumerable.Range(1,approaches.Count-1).Any(delegate(int i) { return approaches[i]==approaches[i-1]; }),"adjacent actions never repeat across round boundaries");

        c=new AnimationController(7);
        T(c,0,149); T(c,1,201); T(c,.1,149); T(c,1.8,149);
        Check(c.InteractionSerial==1,"reentry waits for three-second cooldown");
        T(c,.2,149);
        Check(c.InteractionSerial==2,"pending reentry fires after cooldown");
        T(c,5.9,175);
        Check(c.InteractionSerial==2,"hysteresis preserves entry without premature repeat");
        T(c,.11,195);
        Check(c.InteractionSerial==2,"resting near the pet does not replay an action every six seconds");
        T(c,35.1,195);
        Check(c.InteractionSerial==3,"staying nearby repeats only after the longer randomized interval");
        c=new AnimationController(8);
        c.Tick(0,100,true,false,CompanionWorkState.Idle);
        AnimationPose blocked=c.Tick(10,100,false,true,CompanionWorkState.Working);
        Check(c.InteractionSerial==0 && !blocked.AllowGaze,"drag and panel block approach triggers");
        T(c,0,100);
        Check(c.InteractionSerial==1,"approach resumes after drag and panel close");

        c=new AnimationController(9);
        T(c,0,100);
        double duration=0;
        while (c.Current.Family=="interaction" && duration<5) { duration+=.05; T(c,.05,300); }
        Check(duration>=2 && duration<=4.05,"interaction finishes in requested two-to-four-second range");
        c=new AnimationController(10);
        List<string> workNames=new List<string>();
        string previous=null;
        bool posesValid=true;
        for(int i=0;i<800;i++)
        {
            AnimationPose p=T(c,.1,1000,CompanionWorkState.Working);
            if(p.DisplayName!=previous) { workNames.Add(p.DisplayName); previous=p.DisplayName; }
            posesValid &= p.Frame>=0 && Math.Abs(p.OffsetX)<=6 && Math.Abs(p.OffsetY)<=6 && Math.Abs(p.Rotation)<=4 && p.ScaleX>=.98 && p.ScaleX<=1.02 && p.ScaleY>=.98 && p.ScaleY<=1.02;
            posesValid &= p.Clip=="running" && p.OffsetX==0 && p.OffsetY==0 && p.Rotation==0 && p.ScaleX==1 && p.ScaleY==1;
        }
        Check(workNames.Take(6).Distinct().Count()==6,"working cycles through six variants before repeat");
        Check(posesValid,"work keeps the thinking clip and a fixed head/body transform");
        Check(T(c,0,1000).Family!="work","task completion releases work animation");

        c=new AnimationController(11);
        List<string> idleNames=new List<string>();
        previous=null;
        double firstIdle=-1;
        for(int i=1;i<=8000;i++)
        {
            AnimationPose p=T(c,.1,1000);
            if(p.Family=="idle" && p.DisplayName!=previous)
            {
                idleNames.Add(p.DisplayName); if(firstIdle<0)firstIdle=i*.1;
                previous=p.DisplayName;
            }
            if(p.Family!="idle")previous=null;
        }
        Check(firstIdle>=18 && firstIdle<=58.1,"first idle detail uses the longer randomized quiet interval");
        Check(idleNames.Take(8).Distinct().Count()==8,"idle details use a nonrepeating eight-action bag");
        c=new AnimationController(12);
        c.AddInteraction("Custom",new AnimationStep("waving",1),new AnimationStep("review",1));
        Check(c.InteractionNames.Length==9 && c.InteractionNames.Contains("Custom"),"custom interactions extend built-ins");
        bool rejects=false;
        try { c.AddInteraction("Too short",new AnimationStep("idle",.1)); } catch(ArgumentException) { rejects=true; }
        Check(rejects,"invalid custom action duration rejected");
        AnimationController a=new AnimationController(13), b=new AnimationController(13);
        bool same=true;
        for(int i=0;i<300;i++)
        {
            double distance=i%100<50?100:300;
            AnimationPose pa=T(a,.1,distance),pb=T(b,.1,distance);
            same &= pa.DisplayName==pb.DisplayName && pa.Clip==pb.Clip && pa.Frame==pb.Frame;
        }
        Check(same,"fixed seed yields reproducible clock-driven animation");
        Console.WriteLine("ALL " + checks + " CHECKS PASSED");
    }
}
