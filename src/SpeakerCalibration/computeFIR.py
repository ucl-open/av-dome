from pathlib import Path
import numpy as np
import pandas as pd
from scipy import signal
from scipy.io import wavfile, loadmat
# import matplotlib.pyplot as plt
import matplotlib
matplotlib.use("Agg")   # must be called before importing pyplot
import matplotlib.pyplot as plt


# UMIK-2 calibration file (0 deg) for the measurement microphone, matched to its serial number. Set to None to skip the microphone correction
try:
    _script_dir = Path(__file__).parent
except NameError:  # __file__ is not defined when the script is loaded by Bonsai's Python runtime
    _script_dir = Path.cwd() / "SpeakerCalibration"  # Bonsai's working directory is the workflow's folder (src), matching the relative ScriptPath
MIC_CAL_PATH = _script_dir / "Calibration_File_8109526.txt"


def compute_FIR_filter(speakerId, recording_path=None, save_path=None, mic_cal_path=MIC_CAL_PATH):
    rootFolder = Path(r"C:\RigConfigs")

    # format speaker id with 3 digits, e.g. 001, 002, 003, etc.
    speaker_id_str = f"Speaker_{speakerId:03d}"
    if recording_path is None:
        recording_path = rootFolder / f"{speaker_id_str}.wav"
    # recording_path = rootFolder / f"{speaker_id_str}.wav"
    if save_path is None:
        save_path = rootFolder / f"{speaker_id_str}_FIR_filter.csv"  # path to save resulting kernel filter
    #

    print(f"Computing FIR filter for {speaker_id_str}...")
    print(f"Recording path: {recording_path}")
    print(f"Save path: {save_path}")


    # ## Recording Configurations
    mic_fs = 48000  # microphone sampling rate in Hz
    spk_fs = 48000   # speaker playback rate in Hz
    numtaps = 257 # number of coefficients. Equivalent to filter order + 1
    ref_band_hz = (8000.0, 16000.0) # The frequency band from which to derive a mean magnitude
    clamp_band_hz = (5000.0, 20000.0) # The frequencies above and below which are clamped (to avoid overcorrection due to division errors)
    smooth_bins = 31 # The bandwidth (in spectrogram bins) with which to smooth the peak magnitude spectrum (~360 Hz at nfft=4096)
    decimate_steps = 1 # The spectrogram is already coarse enough, so no decimation needed
    spec_nperseg = 1024 # Spectrogram window length (~21 ms at 48 kHz). Short enough that the sweep does not smear over many bins
    spec_noverlap = 896 # Large overlap (hop = 128 samples) so the sweep's peak in each frequency bin is not missed between frames
    spec_nfft = 4096 # Zero-padded FFT length for a denser frequency grid (~11.7 Hz bins)
    edge_margin_hz = 250.0 # Bins this close to the sweep's start/end frequencies are unreliable (window only partly filled by the sweep), so hold the value from just inside
    max_boost = 5.0 # Maximum amplitude gain of the filter (5x = +14 dB), to avoid clipping and overdriving the speaker


    # ## Load Recording
    rec_path = Path(recording_path)
    recording, mic_fs = load_recording(rec_path)
    print(f"mic_fs: {mic_fs}")
    print(f"recording shape: {recording.shape}")


    recording = recording - np.mean(recording)  # Remove DC offset
    # recording = 0.95 * recording / np.max(np.abs(recording))  # Normalize to [-1, 1]
    print(np.max(np.abs(recording)))
    plt.plot(recording)
    plt.savefig(rootFolder / f"{speaker_id_str}_recording.png")
    plt.close()

    # ##
    # # Define signal threshold for clipping
    # signal_threshold = 1.1*np.max(recording[100:mic_fs+100])  # Remove the first 0.5 seconds of recording to avoid any initial noise
    # # signal_threshold = 0.25
    # print(f"Signal threshold: {signal_threshold}")

    # # Find start and end indices where signal exceeds threshold
    # signal_start_id = np.argwhere(np.abs(recording) > signal_threshold)[0]
    # signal_end_id = np.argwhere(np.abs(recording) > signal_threshold)[-1]

    # print(f"Signal start index: {signal_start_id}")
    # print(f"Signal end index: {signal_end_id}")

    # # Clip the recording to the detected signal region
    # baseline_recording = recording[100:signal_start_id[0]-100]  # The baseline segment before the signal starts, and removing 100 samples at the beginning and end to be save.
    # recording = recording[signal_start_id[0]:signal_end_id[0]+1]

    # # To be compatible with the rest of the script, I keep only the microphone signals in "recording" and store the speaker signal separately.
    # # speaker_signal = recording[0, :]
    # # recording = recording[1, :]
    # # baseline = baseline_recording[1, :]



    # plot the power spectrum as a spectrogram
    plt.figure()
    # plt.plot(freqs_hz, mag)
    plt.specgram(recording, NFFT=1024, Fs=mic_fs, noverlap=512, cmap='viridis')
    plt.title(f"Power Spectrum of {speaker_id_str}")
    plt.xlabel("Time (s)")
    plt.ylabel("Frequency (Hz)")
    plt.savefig(rootFolder / f"{speaker_id_str}_power_spectrum_withoutClip.png")
    plt.close()

    t0, T = find_sweep(recording, mic_fs, F0=clamp_band_hz[0], F1=clamp_band_hz[1])
    start, Tbest = refine_sweep_interval(recording, mic_fs, t0, T, F0=clamp_band_hz[0], F1=clamp_band_hz[1])  # Find the exact start time and duration of the sweep
    background = recording[480:480+int(Tbest * mic_fs)]  # match the length of the recording. IMPORTANT: this makes the assumption that you record at least 2s baseline!!!
    recording = recording[start:start + int(Tbest * mic_fs)]



    # ## Compute Background Spectrum
    # Mean (not peak) over time: the noise that adds to the sweep at any given moment is the typical noise level, not its maximum
    freqs_hz_background, mag_background = spectrogram_magnitude(background, mic_fs, spec_nperseg, spec_noverlap, spec_nfft, stat="mean")

    # plot the power spectrum as a spectrogram
    plt.figure()
    # plt.plot(freqs_hz, mag)
    plt.specgram(background, NFFT=1024, Fs=mic_fs, noverlap=512, cmap='viridis')
    plt.title(f"Power Spectrum of {speaker_id_str} Background")
    plt.xlabel("Time (s)")
    plt.ylabel("Frequency (Hz)")
    plt.savefig(rootFolder / f"{speaker_id_str}_power_spectrum_background.png")
    plt.close()


    # ## Compute Peak Magnitude Spectrum
    # The sweep passes through each frequency only briefly, so take the peak magnitude over time for each frequency bin
    freqs_hz, mag = spectrogram_magnitude(recording, mic_fs, spec_nperseg, spec_noverlap, spec_nfft, stat="max")

    # ## Remove the microphone's own frequency response
    if mic_cal_path is not None:
        print(f"Correcting for microphone response: {mic_cal_path}")
        mag = mag / mic_response(freqs_hz, mic_cal_path)

    # plot the power spectrum as a spectrogram
    plt.figure()
    # plt.plot(freqs_hz, mag)
    plt.specgram(recording, NFFT=1024, Fs=mic_fs, noverlap=512, cmap='viridis')
    plt.title(f"Power Spectrum of {speaker_id_str}")
    plt.xlabel("Time (s)")
    plt.ylabel("Frequency (Hz)")
    plt.savefig(rootFolder / f"{speaker_id_str}_power_spectrum.png")
    plt.close()

    plt.figure()
    plt.plot(freqs_hz, mag, label="Recording (peak)")
    plt.plot(freqs_hz_background, mag_background, label="Background (mean)")
    plt.xlabel("Frequency (Hz)")
    plt.ylabel("Magnitude")
    plt.legend()
    plt.title(f"Peak Magnitude Spectrum of {speaker_id_str}")
    plt.savefig(rootFolder / f"{speaker_id_str}_peak_magnitude.png")
    plt.close()


    # if np.all(freqs_hz_background == freqs_hz):  # Check if the frequency bins are the same for both spectra
    #     print("All good!")
    # else:
    #     raise ValueError("Frequency bins do not match between background and recording spectra.")
    # #

    # mag = mag - mag_background

    # ## Hold magnitudes outside the sweep band
    # Outside the band there is only noise. Holding the edge values (instead of clamping the gain to 1 later) avoids a step in the
    # gain at the band edges, and stops the smoothing below from mixing the noise floor into the in-band values.
    print(f"Holding magnitudes outside {clamp_band_hz[0] + edge_margin_hz} - {clamp_band_hz[1] - edge_margin_hz}Hz at the edge values...")
    valid_idx = np.flatnonzero((freqs_hz >= clamp_band_hz[0] + edge_margin_hz) & (freqs_hz <= clamp_band_hz[1] - edge_margin_hz))
    mag[:valid_idx[0]] = mag[valid_idx[0]]
    mag[valid_idx[-1] + 1:] = mag[valid_idx[-1]]

    # ## Smooth Power Spectrum
    print(f"Smoothing by {smooth_bins}...")
    mag = moving_avg(mag, smooth_bins)
    print(f"Decimating every {decimate_steps} frequency bins...")
    freqs_hz = freqs_hz[::decimate_steps].copy()
    mag = mag[::decimate_steps].copy()
    # plt.plot(freqs_hz,mag)


    # ## Normalizing magnitudes
    print(f"Normalizing magnitudes to specified reference band ({ref_band_hz[0]} - {ref_band_hz[1]}Hz) and inverting...")
    ref_mask = (freqs_hz > ref_band_hz[0]) & (freqs_hz < ref_band_hz[1]) # Mask for the specified frequency band
    mag_norm = mag / np.mean(mag[ref_mask])
    inv_mag = 1.0 / np.maximum(mag_norm, 1e-12) # Invert with a safety floor. mag is an amplitude, so 1/mag is already the amplitude gain (no sqrt)

    print(f"Limiting boost to {max_boost}x...")
    inv_mag = np.minimum(inv_mag, max_boost)
    plt.plot(freqs_hz, inv_mag)
    plt.xlabel("Frequency (Hz)")
    plt.ylabel("Inverse Magnitude")
    plt.title(f"Inverse Magnitude Spectrum of {speaker_id_str}")
    plt.savefig(rootFolder / f"{speaker_id_str}_inverse_magnitude.png")
    plt.close()


    # ## Generate the filter kernel
    fir = design_fir_from_mag(freqs_hz, inv_mag, spk_fs=spk_fs, numtaps=numtaps)
    plt.plot(fir)
    plt.xlabel("Sample")
    plt.ylabel("Amplitude")
    plt.title("FIR Filter Kernel")
    plt.savefig(rootFolder / f"{speaker_id_str}_fir_kernel.png")
    plt.close()

    # ## Save FIR Kernel
    sp = Path(save_path)
    sp.parent.mkdir(parents=True, exist_ok=True)
    df = pd.DataFrame({'FIR_Coefficients': fir})
    # df.to_csv(sp, index=False, float_format="%.9e")
    df.to_csv(sp, index=False, float_format="%.9f") 
#


# ## Helper Functions

def find_sweep(x, fs, snr_db=15, F0=5000, F1=20000):
    # 1) spectrogram + per-frame ridge (as in Option B)
    f, tt, S = signal.spectrogram(x, fs, nperseg=2048, noverlap=1792)
    band = (f >= F0 - 500) & (f <= F1 + 500)
    fb = f[band]
    Sb = 10 * np.log10(S[band] + 1e-20)
    snr = Sb - np.median(Sb, axis=1, keepdims=True)

    k = snr.argmax(axis=0)                      # peak bin per frame
    active = snr[k, np.arange(len(tt))] > snr_db

    # longest contiguous active run
    d = np.diff(np.r_[0, active.astype(int), 0])
    s, e = np.where(d == 1)[0], np.where(d == -1)[0]
    i = np.argmax(e - s)
    idx = np.arange(s[i], e[i])

    # 2) sub-bin peak frequency (parabolic interpolation on dB)
    kk = np.clip(k[idx], 1, len(fb) - 2)
    a, b, c = Sb[kk - 1, idx], Sb[kk, idx], Sb[kk + 1, idx]
    delta = 0.5 * (a - c) / (a - 2 * b + c + 1e-12)
    fpk = fb[kk] + delta * (fb[1] - fb[0])
    t = tt[idx]

    # 3) robust line fit f = m*t + q (reject outliers iteratively)
    keep = np.ones_like(t, bool)
    for _ in range(5):
        m, q = np.polyfit(t[keep], fpk[keep], 1)
        r = fpk - (m * t + q)
        mad = np.median(np.abs(r[keep])) + 1e-9
        keep = np.abs(r) < 4 * 1.4826 * mad

    t_start = (F0 - q) / m
    t_end = (F1 - q) / m
    return t_start, t_end - t_start
#


def refine_sweep_interval(x, fs, t0, T, dT=0.02, n=21, search=0.05, F0=5000, F1=20000):
    lo = max(0, int((t0 - search) * fs))
    best = (-np.inf, None, None)
    for Tc in np.linspace(T - dT, T + dT, n):
        tr = np.arange(int(Tc * fs)) / fs
        ref = signal.chirp(tr, F0, Tc, F1, method="linear")
        hi = min(len(x), lo + len(ref) + int(2 * search * fs))
        c = signal.correlate(x[lo:hi], ref, mode="valid", method="fft")
        c /= np.linalg.norm(ref)
        j = int(np.argmax(np.abs(c)))
        if abs(c[j]) > best[0]:
            best = (abs(c[j]), lo + j, Tc)
    _, start, Tbest = best
    return start, Tbest
#


def moving_avg(x, w):
    """
    Compute a centered moving average over a 1D array using reflection padding.
    Equivalent to MATLAB's smooth(x, w, 'moving').

    Parameters:
    ----------
    x : 1D input array to smooth.
    w : Window length (odd preferred; if even, incremented by 1).

    Returns: Smoothed 1D array of the same length as x.
    """
    if w <= 1:
        return x
    if w % 2 == 0:
        w += 1
    pad = w // 2
    xp = np.pad(x, pad_width=pad, mode="reflect")
    kernel = np.ones(w) / w
    return np.convolve(xp, kernel, mode="valid")
#


def power_spectrum(x, fs):
    """
    Compute the single-sided linear magnitude spectrum of a real-valued 1D signal.
    Equivalent to MATLAB's convFFT helper.

    Parameters:
    ----------
    x : 1D input array (time-domain signal).
    fs : Sampling rate of the signal in Hz.

    Returns:
    -------
    freqs : 1D array of frequency values in Hz.
    mag   : 1D array of linear magnitudes corresponding to freqs.
    """
    x = np.asarray(x, dtype=float)
    L = x.size
    NFFT = 1 << (L - 1).bit_length()    # Zero-pad to next power of two for FFT efficiency
    X = np.fft.rfft(x, n=NFFT)          # Real FFT: positive frequencies only
    X = X / L                           # Normalize amplitude by signal length
    mag = 2.0 * np.abs(X)               # Double to account for discarded negative frequencies
    freqs = np.linspace(0.0, fs / 2.0, num=mag.size, endpoint=True)
    return freqs, mag
#


def spectrogram_magnitude(x, fs, nperseg=1024, noverlap=896, nfft=4096, stat="max"):
    """
    Compute a magnitude spectrogram and collapse it over time for each frequency bin.
    With stat="max" this gives the peak magnitude per frequency, which is what a sweep
    produces as it passes through each frequency.

    Parameters:
    ----------
    x : 1D input array (time-domain signal).
    fs : Sampling rate of the signal in Hz.
    nperseg, noverlap, nfft : Spectrogram window length, overlap and FFT length in samples.
    stat : "max" for the peak magnitude over time, "mean" for the average magnitude over time.

    Returns:
    -------
    freqs : 1D array of frequency values in Hz.
    mag   : 1D array of linear magnitudes corresponding to freqs.
    """
    x = np.asarray(x, dtype=float)
    freqs, _, S = signal.spectrogram(x, fs, window="hann", nperseg=nperseg, noverlap=noverlap,
                                     nfft=nfft, scaling="spectrum", mode="magnitude")
    if stat == "max":
        mag = S.max(axis=1)
    elif stat == "mean":
        mag = S.mean(axis=1)
    else:
        raise ValueError(f"Unknown stat '{stat}', use 'max' or 'mean'.")
    return freqs, mag
#


def mic_response(freqs_hz, mic_cal_path):
    """
    Load a miniDSP (UMIK) calibration file and interpolate the microphone's response onto freqs_hz.
    The file has a header line (sensitivity, gain, serial number) followed by two columns: frequency (Hz) and response (dB).
    The sensitivity is ignored, since the spectra are normalized to a reference band anyway.

    Parameters:
    ----------
    freqs_hz : 1D array of frequency values in Hz.
    mic_cal_path : Path to the calibration .txt file.

    Returns: 1D array of linear amplitude responses (divide measured magnitudes by this).
    """
    cal = np.loadtxt(mic_cal_path, skiprows=1)
    cal_db = np.interp(freqs_hz, cal[:, 0], cal[:, 1])  # Holds the end values outside the calibrated range
    return 10 ** (cal_db / 20)
#


def load_recording(recording_path: Path):
    """
    Load a mono .wav recording with scipy.

    Parameters:
    ----------
    recording_path : String path to the .wav file to be loaded.

    Returns:
    -------
    rec : 1D array (float64) of the audio signal normalized to range [-1, 1]
    fs  : Sampling rate of the recording in Hz.
    """
    if recording_path.suffix.lower() == '.mat':
        mat_data = loadmat(str(recording_path))
        if 'data' in mat_data:
            rec = mat_data['data'].squeeze()
        else:
            raise ValueError("The .mat file does not contain 'data' variable.")
        if 'sampling_freq' in mat_data:
            fs = float(mat_data['sampling_freq'].squeeze())
        else:
            fs = 40000.0  # TEMPORARY FIX DUE TO NAMING ISSUE IN SAVING SCRIPT
            # raise ValueError("The .mat file does not contain 'sampling_freq' variable.")
        rec = rec.astype(np.float64)
        return rec, fs
    else:
        fs, rec = wavfile.read(str(recording_path))
        if np.issubdtype(rec.dtype, np.integer):
            #print("Converting from int to float")
            maxv = np.iinfo(rec.dtype).max  # Scale according to bit depth
            rec = rec.astype(np.float64) / maxv
        # else:
        #     print("Recording is a float") 
        #     rec = rec.astype(np.float64)
        
    return rec, fs
#


def design_fir_from_mag(freqs_hz: np.ndarray, gain: np.ndarray, spk_fs: float, numtaps: int):
    """
    Design a linear-phase FIR filter from a frequency–gain specification.
    This is a direct Python equivalent of MATLAB's:
        kernel = fir2(fOrd, [f(1:end-1)/(spkF/2) 1], invF(i,:))
    """

    # 1. Compute Nyquist frequency (MATLAB uses cali.spkF/2)
    fnyq = spk_fs / 2.0

    # 2. Clip any frequency values to stay within [0, Nyquist]
    #   pow(f>spkF/2) = []; f(f>spkF/2) = [];
    f = np.clip(freqs_hz, 0, fnyq)

    # 3. Avoid having the final frequency point equal to Nyquist exactly. This step and step 5 are not strictly needed by scipy, but it is still here to match the MATLAB method as closely as possible
    # MATLAB’s fir2() expects the last normalized frequency < 1.0 (to avoid duplicate endpoint),
    # so we nudge the last value slightly below fnyq. 
    eps = 1e-9
    f[-1] = min(f[-1], fnyq - eps)

    # 4. Normalize frequency vector to [0, 1]
    # MATLAB: f(1:end-1)/(spkF/2)
    f_norm = f / fnyq

    # 5. Ensure the first and last points are anchored at 0 and 1
    # MATLAB’s fir2() requires the freq array to start at 0 and end at 1.
    # If missing, we pad them with the nearest gain values.
    if f_norm[0] > 0:
        f_norm = np.concatenate([[0.0], f_norm])
        gain = np.concatenate([[gain[0]], gain])
    if f_norm[-1] < 1.0:
        f_norm = np.concatenate([f_norm, [1.0]])
        gain = np.concatenate([gain, [gain[-1]]])

    # 6. Design the FIR filter
    # MATLAB: fir2(fOrd, freq_vector, mag_vector)
    # SciPy equivalent: signal.firwin2(numtaps, freq, gain)
    # The "hann" window is similar to MATLAB’s default Hamming-like weighting.
    fir = signal.firwin2(numtaps=numtaps, freq=f_norm, gain=gain, window="hann")

    # 7. Return the designed 1D FIR kernel (length = numtaps)
    # Equivalent to MATLAB’s fBnk.sFlt(i,:) = fir2(...)
    return fir
#


## #### ##############################

def check_filtered_response(speakerId, mic_cal_path=MIC_CAL_PATH):
    rootFolder = Path(r"C:\RigConfigs")

    # format speaker id with 3 digits, e.g. 001, 002, 003, etc.
    speaker_id_str = f"Speaker_{speakerId:03d}"
    recording_path = rootFolder / f"{speaker_id_str}_with_FIR.wav"
    #
    
    print(f"Checking Recording: {recording_path}")


    # ## Recording Configurations (keep in sync with compute_FIR_filter)
    mic_fs = 48000  # microphone sampling rate in Hz
    ref_band_hz = (8000.0, 16000.0) # The frequency band from which to derive a mean magnitude
    clamp_band_hz = (5000.0, 20000.0) # The sweep's start and end frequencies
    smooth_bins = 31 # The bandwidth (in spectrogram bins) with which to smooth the peak magnitude spectrum (~360 Hz at nfft=4096)
    decimate_steps = 1 # The spectrogram is already coarse enough, so no decimation needed
    spec_nperseg = 1024 # Spectrogram window length (~21 ms at 48 kHz). Short enough that the sweep does not smear over many bins
    spec_noverlap = 896 # Large overlap (hop = 128 samples) so the sweep's peak in each frequency bin is not missed between frames
    spec_nfft = 4096 # Zero-padded FFT length for a denser frequency grid (~11.7 Hz bins)
    edge_margin_hz = 250.0 # Bins this close to the sweep's start/end frequencies are unreliable (window only partly filled by the sweep), so hold the value from just inside


    # ## Load Recording
    rec_path = Path(recording_path)
    recording, mic_fs = load_recording(rec_path)
    print(f"mic_fs: {mic_fs}")
    print(f"recording shape: {recording.shape}")


    recording = recording - np.mean(recording)  # Remove DC offset
    plt.plot(recording)
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_recording.png")
    plt.close()


    # plot the power spectrum as a spectrogram
    plt.figure()
    plt.specgram(recording, NFFT=1024, Fs=mic_fs, noverlap=512, cmap='viridis')
    plt.title(f"Power Spectrum of {speaker_id_str}")
    plt.xlabel("Time (s)")
    plt.ylabel("Frequency (Hz)")
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_power_spectrum_withoutClip.png")
    plt.close()


    # ##
    t0, T = find_sweep(recording, mic_fs, F0=clamp_band_hz[0], F1=clamp_band_hz[1])
    start, Tbest = refine_sweep_interval(recording, mic_fs, t0, T, F0=clamp_band_hz[0], F1=clamp_band_hz[1])  # Find the exact start time and duration of the sweep
    background = recording[480:480+int(Tbest * mic_fs)]  # match the length of the recording. IMPORTANT: this makes the assumption that you record at least 2s baseline!!!
    recording = recording[start:start + int(Tbest * mic_fs)]


    # ## Compute Background Spectrum
    # Mean (not peak) over time: the noise that adds to the sweep at any given moment is the typical noise level, not its maximum
    freqs_hz_background, mag_background = spectrogram_magnitude(background, mic_fs, spec_nperseg, spec_noverlap, spec_nfft, stat="mean")

    # ## Compute Peak Magnitude Spectrum
    # The sweep passes through each frequency only briefly, so take the peak magnitude over time for each frequency bin
    freqs_hz, mag = spectrogram_magnitude(recording, mic_fs, spec_nperseg, spec_noverlap, spec_nfft, stat="max")

    # ## Remove the microphone's own frequency response
    if mic_cal_path is not None:
        print(f"Correcting for microphone response: {mic_cal_path}")
        mag = mag / mic_response(freqs_hz, mic_cal_path)

    # plot the power spectrum as a spectrogram
    plt.figure()
    plt.specgram(recording, NFFT=1024, Fs=mic_fs, noverlap=512, cmap='viridis')
    plt.title(f"Power Spectrum of {speaker_id_str}")
    plt.xlabel("Time (s)")
    plt.ylabel("Frequency (Hz)")
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_power_spectrum.png")
    plt.close()

    plt.figure()
    plt.plot(freqs_hz, mag, label="Recording (peak)")
    plt.plot(freqs_hz_background, mag_background, label="Background (mean)")
    plt.xlabel("Frequency (Hz)")
    plt.ylabel("Magnitude")
    plt.legend()
    plt.title(f"Peak Magnitude Spectrum of {speaker_id_str} with FIR")
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_peak_magnitude.png")
    plt.close()

    # if np.all(freqs_hz_background == freqs_hz):  # Check if the frequency bins are the same for both spectra
    #     print("All good!")
    # else:
    #     raise ValueError("Frequency bins do not match between background and recording spectra.")
    # #
    # mag = mag - mag_background

    # ## Hold magnitudes outside the sweep band
    print(f"Holding magnitudes outside {clamp_band_hz[0] + edge_margin_hz} - {clamp_band_hz[1] - edge_margin_hz}Hz at the edge values...")
    valid_idx = np.flatnonzero((freqs_hz >= clamp_band_hz[0] + edge_margin_hz) & (freqs_hz <= clamp_band_hz[1] - edge_margin_hz))
    mag[:valid_idx[0]] = mag[valid_idx[0]]
    mag[valid_idx[-1] + 1:] = mag[valid_idx[-1]]

    # ## Smooth Power Spectrum
    print(f"Smoothing by {smooth_bins}...")
    mag = moving_avg(mag, smooth_bins)
    print(f"Decimating every {decimate_steps} frequency bins...")
    freqs_hz = freqs_hz[::decimate_steps].copy()
    mag = mag[::decimate_steps].copy()


    # ## Normalizing magnitudes
    print(f"Normalizing magnitudes to specified reference band ({ref_band_hz[0]} - {ref_band_hz[1]}Hz)...")
    ref_mask = (freqs_hz > ref_band_hz[0]) & (freqs_hz < ref_band_hz[1]) # Mask for the specified frequency band
    mag_norm = mag / np.mean(mag[ref_mask])

    # ## Flatness of the filtered response
    # A perfectly flattened speaker gives 0 dB across the sweep band
    band_mask = (freqs_hz >= clamp_band_hz[0]) & (freqs_hz <= clamp_band_hz[1])
    mag_db = 20 * np.log10(np.maximum(mag_norm, 1e-12))
    print(f"Filtered response {clamp_band_hz[0]} - {clamp_band_hz[1]}Hz: min {mag_db[band_mask].min():.1f} dB, max {mag_db[band_mask].max():.1f} dB, "
          f"std {mag_db[band_mask].std():.1f} dB (re. {ref_band_hz[0]} - {ref_band_hz[1]}Hz)")
    plt.figure()
    plt.plot(freqs_hz[band_mask], mag_db[band_mask])
    plt.axhline(0, color="k", linewidth=0.8)
    plt.xlabel("Frequency (Hz)")
    plt.ylabel(f"Magnitude (dB re. {ref_band_hz[0]:.0f} - {ref_band_hz[1]:.0f}Hz)")
    plt.title(f"Filtered Response of {speaker_id_str}")
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_response_dB.png")
    plt.close()

    # Remaining correction a second pass would apply (no boost limit, so it shows the full residual)
    inv_mag = 1.0 / np.maximum(mag_norm, 1e-12) # Invert with a safety floor. mag is an amplitude, so 1/mag is already the amplitude gain (no sqrt)
    plt.figure()
    plt.plot(freqs_hz, inv_mag)
    plt.xlabel("Frequency (Hz)")
    plt.ylabel("Inverse Magnitude")
    plt.title(f"Inverse Magnitude Spectrum of {speaker_id_str}")
    plt.savefig(rootFolder / f"{speaker_id_str}_with_FIR_inverse_magnitude.png")
    plt.close()
#



####################################


# ##
# ## 
# speakerId = 0
# compute_FIR_filter(speakerId)

# speakerId = 0
# check_filtered_response(speakerId)





