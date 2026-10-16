using D2NG.Core;
using D2NG.Core.D2GS.Items;
using System;
using System.Threading.Tasks;

namespace ConsoleBot.Mule;

public interface IMuleService
{
    /// <param name="only">Restricts what is muled; unset mules everything the mule rules accept.
    /// The cube bot passes its recipes' ingredients and results, so a played character used as the
    /// cube character keeps its own stash.</param>
    Task<bool> MuleItemsForClient(Client client, Func<Item, bool> only = null);

    /// <summary>
    /// What has to happen in the farming game before its mule game: unidentified items can be neither
    /// muled nor sold, and only the farming game is sure to have Cain in town.
    /// </summary>
    Task PrepareForMule(Client client);
}
