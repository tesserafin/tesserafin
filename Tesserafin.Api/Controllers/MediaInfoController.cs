using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.MediaInfoDtos;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Devices;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The media info controller: the network bitrate test. Playback is decided by
/// <c>Playback/Sessions</c> alone (tesserafin#129 step 7).
/// </summary>
[Route("")]
[Authorize]
public class MediaInfoController : BaseTesserafinApiController
{
    /// <summary>
    /// Tests the network with a request with the size of the bitrate.
    /// </summary>
    /// <param name="size">The bitrate. Defaults to 102400.</param>
    /// <response code="200">Test buffer returned.</response>
    /// <returns>A <see cref="FileResult"/> with specified bitrate.</returns>
    [HttpGet("Playback/BitrateTest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesFile(MediaTypeNames.Application.Octet)]
    public ActionResult GetBitrateTestBytes([FromQuery][Range(1, 100_000_000, ErrorMessage = "The requested size must be greater than or equal to {1} and less than or equal to {2}")] int size = 102400)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Random.Shared.NextBytes(buffer);
            return File(buffer, MediaTypeNames.Application.Octet);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
