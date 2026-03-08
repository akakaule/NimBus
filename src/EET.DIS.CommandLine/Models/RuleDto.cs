namespace EET.DIS.CommandLine.Models;

public class RuleDto
{
    public string Name { get; set; }   
    public string SubscriptionName { get; set; }
    public bool IsDeprecated { get; set; }
        
    public class RuleDtoComparer : IEqualityComparer<RuleDto>
    {
        public int GetHashCode(RuleDto co)
        {
            if (co == null)
            {
                return 0;
            }
            return co.Name.GetHashCode();
        }

        public bool Equals(RuleDto x1, RuleDto x2)
        {
            if (object.ReferenceEquals(x1, x2))
            {
                return true;
            }
            if (object.ReferenceEquals(x1, null) ||
                object.ReferenceEquals(x2, null))
            {
                return false;
            }
            return x1.Name == x2.Name && x1.SubscriptionName == x2.SubscriptionName;
        }
    }
}
