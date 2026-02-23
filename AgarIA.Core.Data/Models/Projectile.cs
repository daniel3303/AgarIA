namespace AgarIA.Core.Data.Models;

public class Projectile
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double VX { get; set; }
    public double VY { get; set; }
    public string OwnerId { get; set; }
    public double OwnerMassAtFire { get; set; }
    public bool IsAlive { get; set; } = true;
}
