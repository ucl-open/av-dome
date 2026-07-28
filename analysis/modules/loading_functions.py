import pandas as pd
import numpy as np
import re


def load_sessions(root_folder, all_subjects, show_warnings=False):
    df = pd.DataFrame()
    master_columns = None

    for subject in all_subjects:
        sessionFolders = [p for p in (root_folder / f"sub-{subject}").iterdir() if p.is_dir()]

        for session in sessionFolders:
            # try to load session data
            try:
                data = pd.read_csv(session / "TrialSettings" / "TrialSettings.csv", header=None)
            except Exception as e:
                if show_warnings:
                    print(f"Loading error: {session} -> {e}")
                continue

            # skip session if no trials data loaded
            if data.empty:
                continue
            #
            
            if len(data.keys()) < 38:
                continue
            #
            # print(len(data.keys()))
            
            # Use the first row as the header row, then remove any later rows that repeat it.
            header_row = data.iloc[0].astype(str).tolist()
            new_column_names = header_row.copy()
            
            # Rename fileds saved incorrectly:
            try: new_column_names[new_column_names.index("Value.Item1")] = "correctResponse"
            except: pass
            
            try: new_column_names[new_column_names.index("Value.Item1")] = "response"
            except: pass

            try: new_column_names[new_column_names.index("Value.Item2")] = "target_side"
            except: pass
            
            data = data.iloc[1:].copy()
            data.columns = new_column_names

            repeated_header_mask = data.apply(
                lambda row: row.astype(str).tolist() == header_row,
                axis=1
            )
            data = data.loc[~repeated_header_mask].copy()

            # Clean column names by keeping only the final segment after the last dot.
            data = data.rename(columns=lambda col: col.rsplit(".", 1)[-1] if isinstance(col, str) else col)

            # Add the session identifier as a column.
            data["session_path"] = str(session)
            data["subject"] = subject
            data["dateTime"] = str(session).split("_date-")[-1]  # Extract the dateTime from the session path

            try:
                data["trainingStage"] = np.int32(str(session).split("Stage ")[-1].split("_date")[0])
            except:
                # This wasn't part of the session name for the first few Stage 0 sessions. So, 0 as default...
                data["trainingStage"] = 0
            #
            

            if master_columns is None:
                master_columns = list(data.columns)
                df = data.copy()
            else:
                new_columns = [col for col in data.columns if col not in master_columns]
                if new_columns:
                    master_columns = master_columns + new_columns
                    df = df.reindex(columns=master_columns)
                    data = data.reindex(columns=master_columns)

                missing_columns = [col for col in master_columns if col not in data.columns]
                for col in missing_columns:
                    data[col] = np.nan

                df = df.reindex(columns=master_columns)
                data = data.reindex(columns=master_columns)

                df = pd.concat([df, data], ignore_index=True)
        #
    #

    # Fix column name ("Response" is actually "trial ID", "Item1" is "Response", "Item2" is "target_side")
    # df = df.rename(columns={"Response": "trial_id", "Item1": "response", "Item2": "target_side"})
    
    df = edit_columns(df)  # update/remame set columns as needed
    
    return df
# 

def edit_columns(df):
    # Add a column "training_stage" with integers given as "Stage #" and default to 0 if not found.
    if "training_stage" not in df.columns:
        df["training_stage"] = 0


    def get_column_series(frame, col_name):
        if not isinstance(col_name, str):
            return None
        try:
            result = frame[col_name]
        except Exception:
            return None

        if isinstance(result, pd.Series):
            return result
        if isinstance(result, pd.DataFrame) and result.shape[1] == 1:
            return result.iloc[:, 0]
        return None


    stage_col = None
    for col in df.columns:
        if isinstance(col, str) and col.lower().startswith("stage"):
            stage_col = col
            break

    if stage_col is None:
        for col in df.columns:
            series = get_column_series(df, col)
            if series is None:
                continue
            if pd.api.types.is_object_dtype(series.dtype):
                sample = series.dropna().astype(str)
                if sample.str.contains(r"stage", case=False, na=False).any():
                    stage_col = col
                    break

    if stage_col is not None:
        def extract_stage(value):
            if pd.isna(value):
                return 0
            if isinstance(value, str):
                m = re.search(r"stage\s*(\d+)", value.lower())
                if m:
                    return int(m.group(1))
            return 0

        df["training_stage"] = df[stage_col].apply(extract_stage)

    # Simplified session_id assignment from the end of each session_path.
    if "session_path" in df.columns:
        session_paths = df["session_path"].dropna().astype(str).unique()

        def parse_session_id(path):
            try:
                token = str(path).split("/")[-1]
                match = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", token)
                if match:
                    digits = re.sub(r"\D", "", match.group(1))
                    return int(digits)
                digits = re.sub(r"\D", "", token)
                return int(digits) if digits else -1
            except Exception:
                return -1

        ordered_sessions = sorted(session_paths, key=parse_session_id)
        session_id_map = {path: i for i, path in enumerate(ordered_sessions)}
        df["session_id"] = df["session_path"].astype(str).map(session_id_map).astype("int64")
    #

    return df
# 
