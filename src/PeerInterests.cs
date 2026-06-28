using System.Collections.Generic;
using System.Linq;

public class PeerInterests
{
	public IReadOnlySet<string> Requested
	{
		get
		{
			this.Consented = null!;
			return field;
		}
		set => field = value;
	}
		= new HashSet<string>();
	public IReadOnlySet<string> Permitted
	{
		get
		{
			this.Consented = null!;
			return field;
		}
		set => field = value;
	}
		= new HashSet<string>();
	public IReadOnlySet<string> Consented
	{
		get
		{
			if (field == null)
				field = this.Requested.Intersect(this.Permitted).ToHashSet();
			return field;
		}
		private set;
	}
}
