using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace NimBus.Core.Events
{
    public class Property : IProperty
    {
        private readonly PropertyInfo _propertyInfo;

        public Property(PropertyInfo propertyInfo)
        {
            _propertyInfo = propertyInfo;
        }

        public string Name => _propertyInfo.Name;

        public string TypeName => _propertyInfo.PropertyType.Name;

        public string TypeFullName => _propertyInfo.PropertyType.FullName;

        public string Description => _propertyInfo.GetCustomAttribute<DescriptionAttribute>()?.Description;

        public bool IsRequired => _propertyInfo.GetCustomAttribute<RequiredAttribute>() != null;
    }
}
