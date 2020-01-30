package org.smi.extractorcl.fileUtils;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.smi.common.messaging.IProducerModel;
import org.smi.extractorcl.exceptions.LineProcessingException;
import org.smi.extractorcl.execution.ExtractionKey;
import org.smi.extractorcl.messages.ExtractionRequestInfoMessage;
import org.smi.extractorcl.messages.ExtractionRequestMessage;

import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.*;
import java.util.regex.Pattern;

import com.rabbitmq.client.impl.Environment;

/**
 * Handles events from the CSV parser and constructs the Extract Request Message
 * and Extract Request Info Message.
 */
public class ExtractMessagesCsvHandler implements CsvHandler {

	private static Logger _logger = LoggerFactory.getLogger(ExtractMessagesCsvHandler.class);

	private UUID _extractionJobID;
	private HashSet<String> _identifierSet = new HashSet<>();
	private String _projectID;
	private String _extractionDir;
	private String _extractionModality;
	private ExtractionKey _extractionKey;
	private static final Pattern _chiPattern = Pattern.compile("^\\d{10}$");
	private static final Pattern _eupiPattern = Pattern.compile("^([A-Z]|[0-9]){32}$");
	private IProducerModel _extractRequestMessageProducerModel;
	private IProducerModel _extractRequestInfoMessageProducerModel;

	/**
	 * Default constructor
	 *
	 * @param extractionJobID                        Extraction job ID
	 * @param projectID                              Project ID
	 * @param extractionDir                          Folder to write extracted DICOM
	 *                                               files
	 * @param extractionModality                     Modality specifier. Required
	 *                                               when extracting by
	 *                                               StudyInstanceUID
	 * @param extractRequestMessageProducerModel     Producer model used to write
	 *                                               ExtractRequest messages
	 * @param extractRequestInfoMessageProducerModel Producer model used to write
	 *                                               ExtractRequestInfo messages
	 */
	public ExtractMessagesCsvHandler(UUID extractionJobID, String projectID, String extractionDir,
			String extractionModality, IProducerModel extractRequestMessageProducerModel,
			IProducerModel extractRequestInfoMessageProducerModel) {

		_extractionJobID = extractionJobID;
		_projectID = projectID;
		_extractionDir = extractionDir;
		_extractRequestMessageProducerModel = extractRequestMessageProducerModel;
		_extractRequestInfoMessageProducerModel = extractRequestInfoMessageProducerModel;
		_extractionModality = extractionModality;
	}

	@Override
	public void processHeader(String[] header) throws LineProcessingException, IllegalArgumentException {

		if (header.length > 1)
			throw new LineProcessingException(0, header, "Multiple columns detected");

		try {
			_extractionKey = ExtractionKey.valueOf(header[0]);
		} catch (IllegalArgumentException e) {
			_logger.error("Couldn't parse '" + header[0] + "' to an ExtractionKey value. Possible values are: "
					+ Arrays.asList(ExtractionKey.values()));
			throw e;
		}

		if (_extractionKey == ExtractionKey.StudyInstanceUID && _extractionModality == null)
			throw new IllegalArgumentException("Extracting by StudyInstanceUID, but extraction modality not set");

		_logger.debug("extractionKey: " + _extractionKey);
	}

	@Override
	public void processLine(int lineNum, String[] line) throws LineProcessingException {
		// hmmm...
		_identifierSet.add(line[0]);
	}

	@Override
	public void finished() {

	}

	public void sendMessages(boolean autoRun) throws IllegalArgumentException {
		sendMessages(autoRun, 1000);
	}

	public void sendMessages(int maxIdentifiersPerMessage) throws IllegalArgumentException {
		sendMessages(false, maxIdentifiersPerMessage);
	}

	/**
	 * Sends the messages to the message queue.
	 */
	public void sendMessages(boolean autoRun, int maxIdentifiersPerMessage) throws IllegalArgumentException {

		if (maxIdentifiersPerMessage < 1000) {
			throw new IllegalArgumentException(String
					.format("MaxIdentifiersPerMessage must be at least 1000 (given %s)", maxIdentifiersPerMessage));
		}

		if (_identifierSet.isEmpty()) {
			_logger.error("No identifiers added");
			return;
		}

		String now = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mmX").withZone(ZoneOffset.UTC).format(Instant.now());

		// Split identifiers into subsets

		List<Set<String>> split = new ArrayList<>();
		Set<String> subset = null;

		for (String value : _identifierSet) {
			if (subset == null || subset.size() == maxIdentifiersPerMessage)
				split.add(subset = new HashSet<>());
			subset.add(value);
		}

		ExtractionRequestMessage erm = new ExtractionRequestMessage();
		erm.ExtractionJobIdentifier = _extractionJobID;
		erm.ProjectNumber = _projectID;
		erm.ExtractionDirectory = _extractionDir;
		erm.JobSubmittedAt = now;
		erm.KeyTag = _extractionKey.toString();

		// Only need to send 1 of these
		ExtractionRequestInfoMessage erim = new ExtractionRequestInfoMessage();
		erim.ExtractionJobIdentifier = _extractionJobID;
		erim.ProjectNumber = _projectID;
		erim.ExtractionDirectory = _extractionDir;
		erim.JobSubmittedAt = now;
		erim.KeyValueCount = _identifierSet.size();
		erim.KeyTag = _extractionKey.toString();

		System.out.println("ExtractionJobIdentifier:\t" + _extractionJobID);
		System.out.println("ProjectNumber:\t\t\t\t" + _projectID);
		System.out.println("ExtractionDirectory:\t\t" + _extractionDir);
		System.out.println("ExtractionKey:\t\t\t\t" + _extractionKey);
		System.out.println("KeyValueCount:\t\t\t\t" + _identifierSet.size());
		System.out.println("Number of ExtractionRequestMessage(s):\t" + split.size());

		if (!autoRun) {

			Scanner s = new Scanner(System.in);
			System.out.print("Confirm you want to start an extract job with this information (y/n): ");
			String str = s.nextLine();
			s.close();

			if (!str.toLowerCase().equals("y")) {
				_logger.info("Exiting...");
				return;
			}
		}

		_logger.debug("Sending messages");

		for (Set<String> set : split) {
			erm.ExtractionIdentifiers = new ArrayList<>(set);
			_extractRequestMessageProducerModel.SendMessage(erm, "", null);
		}

		_extractRequestInfoMessageProducerModel.SendMessage(erim, "", null);
		_logger.info("Messages sent (Job " + _extractionJobID + ")");
	}

	/**
	 * Checks if the given patient ID looks like a CHI.
	 *
	 * @param patientID
	 * @return true if ID looks like a CHI, false otherwise.
	 */
	@SuppressWarnings("unused")
	private boolean looksLikeCHI(String patientID) {
		return _chiPattern.matcher(patientID.trim()).matches();
	}

	/**
	 * Checks if the given patient ID looks like a EUPI.
	 *
	 * @param patientID
	 * @return true if ID looks like a EUPI, false otherwise.
	 */
	@SuppressWarnings("unused")
	private boolean looksLikeEUPI(String patientID) {
		return _eupiPattern.matcher(patientID.trim()).matches();
	}

}
