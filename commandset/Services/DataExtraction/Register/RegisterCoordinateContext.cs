using System;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Resolves the coordinate transform that should be applied to
    ///     <see cref="XYZ"/> values before they are reported in the
    ///     register response. The chosen frame is either the host
    ///     document's project coordinates (default) or the shared
    ///     coordinates obtained by composing the host's active project
    ///     location with any link instance transform.
    /// </summary>
    /// <remarks>
    ///     Project coordinate transforms are identity for host elements and
    ///     equal to the link instance's <see cref="RevitLinkInstance.GetTotalTransform"/>
    ///     for linked elements. Shared coordinates additionally apply the
    ///     host's active project location transform.
    /// </remarks>
    public sealed class RegisterCoordinateContext
    {
        private readonly CoordinateSystem _system;
        private readonly Transform _sharedTransform;

        /// <summary>
        ///     Build a context for the host document.
        /// </summary>
        /// <param name="system">Selected coordinate frame.</param>
        /// <param name="host">Host document. Used to obtain the shared-frame transform.</param>
        public RegisterCoordinateContext(CoordinateSystem system, Document host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            _system = system;
            _sharedTransform = system == CoordinateSystem.Shared
                ? ResolveSharedTransform(host)
                : null;
        }

        /// <summary>
        ///     Build a context that already knows the shared-frame transform.
        ///     Used when the caller has computed the transform independently.
        /// </summary>
        public RegisterCoordinateContext(CoordinateSystem system, Transform sharedTransform)
        {
            _system = system;
            _sharedTransform = system == CoordinateSystem.Shared ? sharedTransform : null;
        }

        public CoordinateSystem System => _system;

        /// <summary>
        ///     Shared-frame transform applied to host elements, or null when the
        ///     active frame is project.
        /// </summary>
        public Transform SharedTransform => _sharedTransform;

        /// <summary>
        ///     Apply the frame to <paramref name="xyz"/>. Planar mode flattens
        ///     the result onto the XY plane so the response never leaks the
        ///     internal elevation back to the caller.
        /// </summary>
        public XYZ Apply(XYZ xyz, bool planar)
        {
            if (xyz == null) throw new ArgumentNullException(nameof(xyz));
            return RegisterUnitConverter.ApplyTransform(xyz, _sharedTransform, planar);
        }

        /// <summary>
        ///     Apply the frame plus an additional <paramref name="linkTransform"/>.
        ///     Used when an element belongs to a linked instance and the link
        ///     must be transformed into the host's frame first.
        /// </summary>
        public XYZ ApplyLinked(XYZ xyz, Transform linkTransform, bool planar)
        {
            if (xyz == null) throw new ArgumentNullException(nameof(xyz));
            if (linkTransform == null) throw new ArgumentNullException(nameof(linkTransform));

            // Link brings coordinates into host project space.
            var inHost = RegisterUnitConverter.ApplyTransform(xyz, linkTransform, planar: false);
            // Then project/shared shift is applied on top.
            return _sharedTransform == null
                ? (planar ? new XYZ(inHost.X, inHost.Y, 0) : inHost)
                : RegisterUnitConverter.ApplyTransform(inHost, _sharedTransform, planar);
        }

        /// <summary>
        ///     The project location name to surface in the snapshot, when the
        ///     active frame is shared. Returns null otherwise.
        /// </summary>
        public string ResolveProjectLocationName(Document host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (_system != CoordinateSystem.Shared) return null;
            try
            {
                var location = host.ActiveProjectLocation;
                return location?.Name;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static Transform ResolveSharedTransform(Document host)
        {
            try
            {
                var location = host.ActiveProjectLocation;
                return location?.GetTransform();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}